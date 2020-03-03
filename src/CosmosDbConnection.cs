﻿using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Hangfire.Azure.Queue;
using Hangfire.Azure.Helper;
using Hangfire.Azure.Documents;
using Hangfire.Azure.Documents.Helper;

using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;

using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;

namespace Hangfire.Azure
{
    internal sealed class CosmosDbConnection : JobStorageConnection
    {
        public CosmosDbStorage Storage { get; }
        public PersistentJobQueueProviderCollection QueueProviders { get; }

        public CosmosDbConnection(CosmosDbStorage storage)
        {
            Storage = storage;
            QueueProviders = storage.QueueProviders;
        }

        public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout) => new CosmosDbDistributedLock(resource, timeout, Storage);
        public override IWriteOnlyTransaction CreateWriteTransaction() => new CosmosDbWriteOnlyTransaction(this);

        #region Job

        public override string CreateExpiredJob(Common.Job job, IDictionary<string, string> parameters, DateTime createdAt, TimeSpan expireIn)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            InvocationData invocationData = InvocationData.SerializeJob(job);
            Documents.Job entityJob = new Documents.Job
            {
                InvocationData = invocationData,
                Arguments = invocationData.Arguments,
                CreatedOn = createdAt,
                ExpireOn = createdAt.Add(expireIn),

                Parameters = parameters.Select(p => new Parameter
                {
                    Name = p.Key,
                    Value = p.Value
                }).ToArray()
            };

            Task<ItemResponse<Documents.Job>> task = Storage.Container.CreateItemWithRetriesAsync(entityJob);
            task.Wait();

            if (task.Result.StatusCode == HttpStatusCode.Created || task.Result.StatusCode == HttpStatusCode.OK)
            {
                return entityJob.Id;
            }

            return string.Empty;
        }

        public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
        {
            if (queues == null || queues.Length == 0) throw new ArgumentNullException(nameof(queues));

            IPersistentJobQueueProvider[] providers = queues.Select(q => QueueProviders.GetProvider(q))
                .Distinct()
                .ToArray();

            if (providers.Length != 1)
            {
                throw new InvalidOperationException($"Multiple provider instances registered for queues: {string.Join(", ", queues)}. You should choose only one type of persistent queues per server instance.");
            }

            IPersistentJobQueue persistentQueue = providers.Single().GetJobQueue();
            IFetchedJob queue = persistentQueue.Dequeue(queues, cancellationToken);
            return queue;
        }

        public override JobData GetJobData(string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            Task<ItemResponse<Documents.Job>> task = Storage.Container.ReadItemWithRetriesAsync<Documents.Job>(jobId, PartitionKey.None);
            task.Wait();

            if (task.Result.Resource != null)
            {
                Documents.Job data = task.Result;
                InvocationData invocationData = data.InvocationData;
                invocationData.Arguments = data.Arguments;

                Common.Job job = null;
                JobLoadException loadException = null;

                try
                {
                    job = invocationData.DeserializeJob();
                }
                catch (JobLoadException ex)
                {
                    loadException = ex;
                }

                return new JobData
                {
                    Job = job,
                    State = data.StateName,
                    CreatedAt = data.CreatedOn,
                    LoadException = loadException
                };
            }

            return null;
        }

        public override StateData GetStateData(string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            Task<ItemResponse<Documents.Job>> task = Storage.Container.ReadItemWithRetriesAsync<Documents.Job>(jobId, PartitionKey.None);
            task.Wait();

            if (task.Result.Resource != null)
            {
                Documents.Job job = task.Result;

                // get the state document
                Task<ItemResponse<State>> stateTask = Storage.Container.ReadItemWithRetriesAsync<State>(job.StateId, PartitionKey.None);
                stateTask.Wait();

                if (stateTask.Result.Resource != null)
                {
                    State state = stateTask.Result;
                    return new StateData
                    {
                        Name = state.Name,
                        Reason = state.Reason,
                        Data = state.Data
                    };
                }
            }

            return null;
        }

        #endregion

        #region Parameter

        public override string GetJobParameter(string id, string name)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (name == null) throw new ArgumentNullException(nameof(name));

            Task<ItemResponse<Documents.Job>> task = Storage.Container.ReadItemWithRetriesAsync<Documents.Job>(id, PartitionKey.None);
            Documents.Job data = task.Result;

            return data?.Parameters.Where(p => p.Name == name).Select(p => p.Value).FirstOrDefault();
        }

        public override void SetJobParameter(string id, string name, string value)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (name == null) throw new ArgumentNullException(nameof(name));

            Parameter parameter = new Parameter
            {
                Value = value,
                Name = name
            };

            Task<StoredProcedureExecuteResponse<bool>> task = Storage.Container.Scripts.ExecuteStoredProcedureAsync<bool>("setJobParameter", PartitionKey.None, (dynamic)id, (dynamic)parameter);
            task.Wait();
        }

        #endregion

        #region Set

        public override TimeSpan GetSetTtl(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            QueryDefinition sql = new QueryDefinition("SELECT TOP 1 VALUE MIN(doc['expire_on']) FROM doc WHERE doc.type = @type AND doc.key = @key")
                .WithParameter("@key", key)
                .WithParameter("@type", (int)DocumentTypes.Set);

            int? expireOn = Storage.Container.GetItemQueryIterator<int?>(sql)
                .ToQueryResult()
                .FirstOrDefault();

            return expireOn.HasValue ? expireOn.Value.ToDateTime() - DateTime.UtcNow : TimeSpan.FromSeconds(-1);
        }

        public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            endingAt += 1 - startingFrom;

            return Storage.Container.GetItemLinqQueryable<Set>()
                .Where(s => s.DocumentType == DocumentTypes.Set && s.Key == key)
                .OrderBy(s => s.CreatedOn)
                .Skip(startingFrom).Take(endingAt)
                .Select(s => s.Value)
                .ToQueryResult()
                .ToList();
        }

        public override long GetCounter(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            QueryDefinition sql = new QueryDefinition("SELECT TOP 1 VALUE SUM(doc['value']) FROM doc WHERE doc.type = @type AND doc.key = @key")
                .WithParameter("@key", key)
                .WithParameter("@type", (int)DocumentTypes.Counter);

            return Storage.Container.GetItemQueryIterator<long>(sql)
                .ToQueryResult()
                .FirstOrDefault();
        }

        public override long GetSetCount(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            QueryDefinition sql = new QueryDefinition("SELECT TOP 1 VALUE COUNT(1) FROM doc WHERE doc.type = @type AND doc.key = @key")
                .WithParameter("@key", key)
                .WithParameter("@type", (int)DocumentTypes.Set);

            return Storage.Container.GetItemQueryIterator<long>(sql)
                .ToQueryResult()
                .FirstOrDefault();
        }

        public override HashSet<string> GetAllItemsFromSet(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            IEnumerable<string> sets = Storage.Container.GetItemLinqQueryable<Set>()
                .Where(s => s.DocumentType == DocumentTypes.Set && s.Key == key)
                .Select(s => s.Value)
                .ToQueryResult();

            return new HashSet<string>(sets);
        }

        public override string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (toScore < fromScore) throw new ArgumentException("The `toScore` value must be higher or equal to the `fromScore` value.");

            QueryDefinition sql = new QueryDefinition("SELECT TOP 1 VALUE doc['value'] FROM doc WHERE doc.type = @type AND doc.key = @key AND (doc.score BETWEEN @from AND @to) ORDER BY doc.score")
                .WithParameter("@key", key)
                .WithParameter("@type", (int)DocumentTypes.Set)
                .WithParameter("@from", (int)fromScore)
                .WithParameter("@to", (int)toScore);

            return Storage.Container.GetItemQueryIterator<string>(sql)
                .ToQueryResult()
                .FirstOrDefault();
        }

        #endregion

        #region Server

        public override void AnnounceServer(string serverId, ServerContext context)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));
            if (context == null) throw new ArgumentNullException(nameof(context));

            Documents.Server server = new Documents.Server
            {
                Id = $"{serverId}:{DocumentTypes.Server}".GenerateHash(),
                ServerId = serverId,
                Workers = context.WorkerCount,
                Queues = context.Queues,
                CreatedOn = DateTime.UtcNow,
                LastHeartbeat = DateTime.UtcNow
            };
            Task<ItemResponse<Documents.Server>> task = Storage.Container.UpsertItemWithRetriesAsync(server, new PartitionKey((int)DocumentTypes.Server));
            task.Wait();
        }

        public override void Heartbeat(string serverId)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));
            string id = $"{serverId}:{DocumentTypes.Server}".GenerateHash();

            Task<StoredProcedureExecuteResponse<bool>> task = Storage.Container.Scripts.ExecuteStoredProcedureAsync<bool>("heartbeatServer", new PartitionKey((int)DocumentTypes.Server), new dynamic[] { id, DateTime.UtcNow.ToEpoch() });
            task.Wait();
        }

        public override void RemoveServer(string serverId)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));
            string id = $"{serverId}:{DocumentTypes.Server}".GenerateHash();

            Task<ItemResponse<Documents.Server>> task = Storage.Container.DeleteItemWithRetriesAsync<Documents.Server>(id, new PartitionKey((int)DocumentTypes.Server));
            task.Wait();
        }

        public override int RemoveTimedOutServers(TimeSpan timeOut)
        {
            if (timeOut.Duration() != timeOut)
            {
                throw new ArgumentException(@"invalid timeout", nameof(timeOut));
            }

            int lastHeartbeat = DateTime.UtcNow.Add(timeOut.Negate()).ToEpoch();
            string query = $"SELECT doc._self FROM doc WHERE doc.type = {(int)DocumentTypes.Server} AND IS_DEFINED(doc.last_heartbeat) AND doc.last_heartbeat <= {lastHeartbeat}";

            return Storage.Container.ExecuteDeleteDocuments(query, new PartitionKey((int)DocumentTypes.Server));
        }

        #endregion

        #region Hash

        public override Dictionary<string, string> GetAllEntriesFromHash(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return Storage.Container.GetItemLinqQueryable<Hash>()
                .Where(h => h.DocumentType == DocumentTypes.Hash && h.Key == key)
                .Select(h => new { h.Field, h.Value })
                .ToQueryResult()
                .ToDictionary(h => h.Field, h => h.Value);
        }

        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (keyValuePairs == null) throw new ArgumentNullException(nameof(keyValuePairs));

            Data<Hash> data = new Data<Hash>();

            List<Hash> hashes = Storage.Container.GetItemLinqQueryable<Hash>()
                .Where(h => h.DocumentType == DocumentTypes.Hash && h.Key == key)
                .ToQueryResult()
                .ToList();

            Hash[] sources = keyValuePairs.Select(k => new Hash
            {
                Key = key,
                Field = k.Key,
                Value = k.Value.TryParseToEpoch()
            }).ToArray();

            foreach (Hash source in sources)
            {
                Hash hash = hashes.SingleOrDefault(h => h.Field == source.Field);
                if (hash == null)
                {
                    data.Items.Add(source);
                }
                else if (!string.Equals(hash.Value, source.Value, StringComparison.InvariantCultureIgnoreCase))
                {
                    hash.Value = source.Value;
                    data.Items.Add(hash);
                }
            }

            Storage.Container.ExecuteUpsertDocuments(data, PartitionKey.None);
        }

        public override long GetHashCount(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            QueryDefinition sql = new QueryDefinition("SELECT TOP 1 VALUE COUNT(1) FROM doc WHERE doc.type = @type AND doc.key = @key")
                    .WithParameter("@key", key)
                    .WithParameter("@type", (int)DocumentTypes.Hash);

            return Storage.Container.GetItemQueryIterator<long>(sql)
                .ToQueryResult()
                .FirstOrDefault();
        }

        public override string GetValueFromHash(string key, string name)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (name == null) throw new ArgumentNullException(nameof(name));

            QueryDefinition sql = new QueryDefinition("SELECT TOP 1 VALUE doc['value'] FROM doc WHERE doc.type = @type AND doc.key = @key AND doc.field = @field")
                .WithParameter("@key", key)
                .WithParameter("@field", name)
                .WithParameter("@type", (int)DocumentTypes.Hash);

            return Storage.Container.GetItemQueryIterator<string>(sql)
                .ToQueryResult()
                .FirstOrDefault();
        }

        public override TimeSpan GetHashTtl(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            QueryDefinition sql = new QueryDefinition("SELECT TOP 1 VALUE MIN(doc['expire_on']) FROM doc WHERE doc.type = @type AND doc.key = @key")
                .WithParameter("@key", key)
                .WithParameter("@type", (int)DocumentTypes.Hash);

            int? expireOn = Storage.Container.GetItemQueryIterator<int?>(sql)
                .ToQueryResult()
                .FirstOrDefault();

            return expireOn.HasValue ? expireOn.Value.ToDateTime() - DateTime.UtcNow : TimeSpan.FromSeconds(-1);
        }

        #endregion

        #region List

        public override List<string> GetAllItemsFromList(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return Storage.Container.GetItemLinqQueryable<List>()
                .Where(l => l.DocumentType == DocumentTypes.List && l.Key == key)
                .OrderByDescending(l => l.CreatedOn)
                .Select(l => l.Value)
                .ToQueryResult()
                .ToList();
        }

        public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            endingAt += 1 - startingFrom;

            return Storage.Container.GetItemLinqQueryable<List>()
                .Where(l => l.DocumentType == DocumentTypes.List && l.Key == key)
                .OrderByDescending(l => l.CreatedOn)
                .Skip(startingFrom).Take(endingAt)
                .Select(l => l.Value)
                .ToQueryResult()
                .ToList();
        }

        public override TimeSpan GetListTtl(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            QueryDefinition sql = new QueryDefinition("SELECT TOP 1 VALUE MIN(doc['expire_on']) FROM doc WHERE doc.type = @type AND doc.key = @key")
                .WithParameter("@key", key)
                .WithParameter("@type", (int)DocumentTypes.List);

            int? expireOn = Storage.Container.GetItemQueryIterator<int?>(sql)
                .ToQueryResult()
                .FirstOrDefault();

            return expireOn.HasValue ? expireOn.Value.ToDateTime() - DateTime.UtcNow : TimeSpan.FromSeconds(-1);
        }

        public override long GetListCount(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            QueryDefinition sql = new QueryDefinition("SELECT TOP 1 VALUE COUNT(1) FROM doc WHERE doc.type = @type AND doc.key = @key")
                .WithParameter("@key", key)
                .WithParameter("@type", (int)DocumentTypes.List);

            return Storage.Container.GetItemQueryIterator<long>(sql)
                .ToQueryResult()
                .FirstOrDefault();
        }

        #endregion

    }
}
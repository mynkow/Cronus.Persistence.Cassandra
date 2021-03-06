using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Cassandra;
using Elders.Cronus.EventStore;
using Elders.Cronus.Serializer;
using Elders.Cronus.Persistence.Cassandra.Logging;
using Elders.Cronus.Projections;

namespace Elders.Cronus.Persistence.Cassandra
{
    public class CassandraEventStore : IEventStore
    {
        static readonly ILog log = LogProvider.GetLogger(typeof(CassandraEventStore));

        private readonly string boundedContext;

        private const string LoadAggregateEventsQueryTemplate = @"SELECT data FROM {0} WHERE id = ?;";

        private const string InsertEventsQueryTemplate = @"INSERT INTO {0} (id,ts,rev,data) VALUES (?,?,?,?);";

        private readonly ISerializer serializer;

        private readonly ISession session;

        private readonly ICassandraEventStoreTableNameStrategy tableNameStrategy;

        private readonly ConsistencyLevel writeConsistencyLevel;

        private readonly ConsistencyLevel readConsistencyLevel;

        private readonly ConcurrentDictionary<string, PreparedStatement> persistAggregateEventsPreparedStatements;
        private readonly ConcurrentDictionary<string, PreparedStatement> loadAggregateEventsPreparedStatements;

        public CassandraEventStore(string boundedContext, ISession session, ICassandraEventStoreTableNameStrategy tableNameStrategy, ISerializer serializer, ConsistencyLevel writeConsistencyLevel, ConsistencyLevel readConsistencyLevel)
        {
            if (string.IsNullOrEmpty(boundedContext)) throw new ArgumentNullException(nameof(boundedContext));
            if (ReferenceEquals(null, session)) throw new ArgumentNullException(nameof(session));
            if (ReferenceEquals(null, tableNameStrategy)) throw new ArgumentNullException(nameof(tableNameStrategy));
            if (ReferenceEquals(null, serializer)) throw new ArgumentNullException(nameof(serializer));
            if (ReferenceEquals(null, writeConsistencyLevel)) throw new ArgumentNullException(nameof(writeConsistencyLevel));
            if (ReferenceEquals(null, readConsistencyLevel)) throw new ArgumentNullException(nameof(readConsistencyLevel));

            this.tableNameStrategy = tableNameStrategy;
            this.boundedContext = boundedContext;
            this.session = session;
            this.serializer = serializer;
            this.writeConsistencyLevel = writeConsistencyLevel;
            this.readConsistencyLevel = readConsistencyLevel;
            this.persistAggregateEventsPreparedStatements = new ConcurrentDictionary<string, PreparedStatement>();
            this.loadAggregateEventsPreparedStatements = new ConcurrentDictionary<string, PreparedStatement>();
        }

        private PreparedStatement GetPreparedStatementToPersistAnAggregateCommit(AggregateCommit aggregateCommit)
        {
            PreparedStatement persistAggregatePreparedStatement;
            if (persistAggregateEventsPreparedStatements.TryGetValue(aggregateCommit.BoundedContext, out persistAggregatePreparedStatement) == false)
            {
                string tableName = tableNameStrategy.GetEventsTableName(boundedContext);
                persistAggregatePreparedStatement = session.Prepare(String.Format(InsertEventsQueryTemplate, tableName));
                persistAggregateEventsPreparedStatements.TryAdd(aggregateCommit.BoundedContext, persistAggregatePreparedStatement);
            }

            persistAggregatePreparedStatement.SetConsistencyLevel(writeConsistencyLevel);

            return persistAggregatePreparedStatement;
        }

        private byte[] SerializeEvent(AggregateCommit commit)
        {
            using (var stream = new MemoryStream())
            {
                serializer.Serialize(stream, commit);
                return stream.ToArray();
            }
        }

        public void Append(AggregateCommit aggregateCommit)
        {
            byte[] data = SerializeEvent(aggregateCommit);

            try
            {
                session
                    .Execute(GetPreparedStatementToPersistAnAggregateCommit(aggregateCommit)
                        .Bind(Convert.ToBase64String(aggregateCommit.AggregateRootId), aggregateCommit.Timestamp, aggregateCommit.Revision, data));
            }
            catch (WriteTimeoutException ex)
            {
                log.WarnException("Write timeout while persisting an aggregate commit", ex);
            }
        }

        [Obsolete("Use => EventStream Load(IAggregateRootId aggregateId)")]
        public EventStream Load(IAggregateRootId aggregateId, Func<IAggregateRootId, string> getBoundedContext)
        {
            if (ReferenceEquals(null, getBoundedContext)) throw new ArgumentNullException(nameof(getBoundedContext));

            List<AggregateCommit> aggregateCommits = new List<AggregateCommit>();
            string boundedContext = getBoundedContext(aggregateId);
            BoundStatement bs = GetPreparedStatementToLoadAnAggregateCommit(boundedContext).Bind(Convert.ToBase64String(aggregateId.RawId));
            var result = session.Execute(bs);
            foreach (var row in result.GetRows())
            {
                var data = row.GetValue<byte[]>("data");
                using (var stream = new MemoryStream(data))
                {
                    aggregateCommits.Add((AggregateCommit)serializer.Deserialize(stream));
                }
            }
            return new EventStream(aggregateCommits);
        }

        public EventStream Load(IAggregateRootId aggregateId)
        {
            List<AggregateCommit> aggregateCommits = new List<AggregateCommit>();
            BoundStatement bs = GetPreparedStatementToLoadAnAggregateCommit(boundedContext).Bind(Convert.ToBase64String(aggregateId.RawId));
            var result = session.Execute(bs);
            foreach (var row in result.GetRows())
            {
                var data = row.GetValue<byte[]>("data");
                using (var stream = new MemoryStream(data))
                {
                    aggregateCommits.Add((AggregateCommit)serializer.Deserialize(stream));
                }
            }
            return new EventStream(aggregateCommits);
        }

        private PreparedStatement GetPreparedStatementToLoadAnAggregateCommit(string boundedContext)
        {
            PreparedStatement loadAggregatePreparedStatement;
            if (loadAggregateEventsPreparedStatements.TryGetValue(boundedContext, out loadAggregatePreparedStatement) == false)
            {
                string tableName = tableNameStrategy.GetEventsTableName(boundedContext);
                loadAggregatePreparedStatement = session.Prepare(String.Format(LoadAggregateEventsQueryTemplate, tableName));
                loadAggregateEventsPreparedStatements.TryAdd(boundedContext, loadAggregatePreparedStatement);
            }

            loadAggregatePreparedStatement.SetConsistencyLevel(readConsistencyLevel);

            return loadAggregatePreparedStatement;
        }
    }
}

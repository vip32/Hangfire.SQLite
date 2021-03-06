// This file is part of Hangfire.
// Copyright © 2013-2014 Sergey Odinokov.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Linq;
using System.Threading;
using System.Transactions;
using Dapper;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.SqlServer;
using Hangfire.SQLite.Entities;
using Hangfire.Storage;
using IsolationLevel = System.Transactions.IsolationLevel;

namespace Hangfire.SQLite
{
	internal class SQLiteConnection : JobStorageConnection
	{
		private readonly IDbConnection _connection;
		private readonly IsolationLevel? _isolationLevel;
		private readonly PersistentJobQueueProviderCollection _queueProviders;

		public SQLiteConnection(
			IDbConnection connection,
			IsolationLevel? isolationLevel,
			PersistentJobQueueProviderCollection queueProviders)
			: this(connection, isolationLevel, queueProviders, true)
		{}

		public SQLiteConnection(
			IDbConnection connection,
			IsolationLevel? isolationLevel,
			PersistentJobQueueProviderCollection queueProviders,
			bool ownsConnection)
		{
			if (connection == null) throw new ArgumentNullException("connection");
			if (queueProviders == null) throw new ArgumentNullException("queueProviders");

			_connection = connection;
			_isolationLevel = isolationLevel;
			_queueProviders = queueProviders;

			OwnsConnection = ownsConnection;
		}

		public IDbConnection Connection { get { return _connection; } }
		public bool OwnsConnection { get; private set; }

		public override void Dispose()
		{
			if (OwnsConnection)
			{
				_connection.Dispose();
			}
		}

		public override IWriteOnlyTransaction CreateWriteTransaction()
		{
			return new SQLiteWriteOnlyTransaction(_connection, _isolationLevel, _queueProviders);
		}

		public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
		{
			return new SQLiteDistributedLock();
		}

		public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
		{
			if (queues == null || queues.Length == 0) throw new ArgumentNullException("queues");

			var providers = queues
				.Select(queue => _queueProviders.GetProvider(queue))
				.Distinct()
				.ToArray();

			if (providers.Length != 1)
			{
				throw new InvalidOperationException(String.Format(
					"Multiple provider instances registered for queues: {0}. You should choose only one type of persistent queues per server instance.",
					String.Join(", ", queues)));
			}

			var persistentQueue = providers[0].GetJobQueue(_connection);
			return persistentQueue.Dequeue(queues, cancellationToken);
		}

		public override string CreateExpiredJob(
			Job job,
			IDictionary<string, string> parameters, 
			DateTime createdAt,
			TimeSpan expireIn)
		{
			if (job == null) throw new ArgumentNullException("job");
			if (parameters == null) throw new ArgumentNullException("parameters");

			const string createJobSql = @"
insert into [HangFire.Job] (InvocationData, Arguments, CreatedAt, ExpireAt)
values (@invocationData, @arguments, @createdAt, @expireAt);
select last_insert_rowid()";

			var invocationData = InvocationData.Serialize(job);

			var jobId = _connection.Query<int>(
				createJobSql,
				new
				{
					invocationData = JobHelper.ToJson(invocationData),
					arguments = invocationData.Arguments,
					createdAt = createdAt,
					expireAt = createdAt.Add(expireIn)
				}).Single().ToString();

			if (parameters.Count > 0)
			{
				var parameterArray = new object[parameters.Count];
				int parameterIndex = 0;
				foreach (var parameter in parameters)
				{
					parameterArray[parameterIndex++] = new
					{
						jobId = jobId,
						name = parameter.Key,
						value = parameter.Value
					};
				}

				const string insertParameterSql = @"
insert into [HangFire.JobParameter] (JobId, Name, Value)
values (@jobId, @name, @value)";

				_connection.Execute(insertParameterSql, parameterArray);
			}

			return jobId;
		}

		public override JobData GetJobData(string id)
		{
			if (id == null) throw new ArgumentNullException("id");

			const string sql = 
				@"select InvocationData, StateName, Arguments, CreatedAt from [HangFire.Job] where Id = @id";

			var jobData = _connection.Query<SqlJob>(sql, new { id = id })
				.SingleOrDefault();

			if (jobData == null) return null;

			// TODO: conversion exception could be thrown.
			var invocationData = JobHelper.FromJson<InvocationData>(jobData.InvocationData);
			invocationData.Arguments = jobData.Arguments;

			Job job = null;
			JobLoadException loadException = null;

			try
			{
				job = invocationData.Deserialize();
			}
			catch (JobLoadException ex)
			{
				loadException = ex;
			}

			return new JobData
			{
				Job = job,
				State = jobData.StateName,
				CreatedAt = jobData.CreatedAt,
				LoadException = loadException
			};
		}

		public override StateData GetStateData(string jobId)
		{
			if (jobId == null) throw new ArgumentNullException("jobId");

			const string sql = @"
select s.Name, s.Reason, s.Data
from [HangFire.State] s
inner join [HangFire.Job] j on j.StateId = s.Id
where j.Id = @jobId";

			var sqlState = _connection.Query<SqlState>(sql, new { jobId = jobId }).SingleOrDefault();
			if (sqlState == null)
			{
				return null;
			}

			var data = new Dictionary<string, string>(
				JobHelper.FromJson<Dictionary<string, string>>(sqlState.Data),
				StringComparer.OrdinalIgnoreCase);

			return new StateData
			{
				Name = sqlState.Name,
				Reason = sqlState.Reason,
				Data = data
			};
		}

		public override void SetJobParameter(string jobId, string name, string value)
		{
			if (jobId == null) throw new ArgumentNullException("jobId");
			if (name == null) throw new ArgumentNullException("name");

			var fetchedParam = _connection.Query<JobParameter>("select * from [HangFire.JobParameter] where JobId = @jobId and Name = @name",
				new { jobId = jobId, name = name }).Any();

			if (!fetchedParam)
			{
				// insert
				_connection.Execute(
				@"insert into [HangFire.JobParameter] (JobId, Name, Value) values (@jobId, @name, @value);",
				new { jobId = jobId, name, value });
			}
			else
			{
				// update
				_connection.Execute(@"update [HangFire.JobParameter] set Name = @name, Value = @value where JobId = @jobId;",
				new { jobId = jobId, name, value });
			}
		}

		public override string GetJobParameter(string jobId, string name)
		{
			if (jobId == null) throw new ArgumentNullException("jobId");
			if (name == null) throw new ArgumentNullException("name");

			return _connection.Query<string>(
				@"select Value from [HangFire.JobParameter] where JobId = @jobId and Name = @name",
				new { jobId = jobId, name = name })
				.SingleOrDefault();
		}

		public override HashSet<string> GetAllItemsFromSet(string key)
		{
			if (key == null) throw new ArgumentNullException("key");

			var result = _connection.Query<string>(
				@"select Value from [HangFire.Set] where [Key] = @key",
				new { key });
			
			return new HashSet<string>(result);
		}

		public override string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore)
		{
			if (key == null) throw new ArgumentNullException("key");
			if (toScore < fromScore) throw new ArgumentException("The `toScore` value must be higher or equal to the `fromScore` value.");

			return _connection.Query<string>(
				@"select Value from [HangFire.Set] where [Key] = @key and Score between @from and @to order by Score limit 1",
				new { key, from = fromScore, to = toScore })
				.SingleOrDefault();
		}

		public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
		{
			if (key == null) throw new ArgumentNullException("key");
			if (keyValuePairs == null) throw new ArgumentNullException("keyValuePairs");

//            const string sql = @"
//;merge [HangFire.Hash] with (holdlock) as Target
//using (VALUES (@key, @field, @value)) as Source ([Key], Field, Value)
//on Target.[Key] = Source.[Key] and Target.Field = Source.Field
//when matched then update set Value = Source.Value
//when not matched then insert ([Key], Field, Value) values (Source.[Key], Source.Field, Source.Value);";

			using (var transaction = new TransactionScope())
			{
				foreach (var keyValuePair in keyValuePairs)
				{
				    var fetchedHash = _connection.Query<SqlHash>("select * from [HangFire.Hash] where [Key] = @key and Field = @field",
				        new {key = key, field = keyValuePair.Key});
				    if (!fetchedHash.Any())
				    {
				        _connection.Execute("insert into [HangFire.Hash] ([Key], Field, Value) values (@key, @field, @value)",
				            new {key = key, field = keyValuePair.Key, value = keyValuePair.Value});
				    }
				    else
				    {
				        _connection.Execute("update [HangFire.Hash] set Value = @value where values [Key] = @key and Field = @field ",
				            new {key = key, field = keyValuePair.Key, value = keyValuePair.Value});
				    }
				}

				transaction.Complete();
			}
		}

		public override Dictionary<string, string> GetAllEntriesFromHash(string key)
		{
			if (key == null) throw new ArgumentNullException("key");

			var result = _connection.Query<SqlHash>(
				"select Field, Value from [HangFire.Hash] where [Key] = @key",
				new { key })
				.ToDictionary(x => x.Field, x => x.Value);

			return result.Count != 0 ? result : null;
		}

		public override void AnnounceServer(string serverId, ServerContext context)
		{
			if (serverId == null) throw new ArgumentNullException("serverId");
			if (context == null) throw new ArgumentNullException("context");

			var data = new ServerData
			{
				WorkerCount = context.WorkerCount,
				Queues = context.Queues,
				StartedAt = DateTime.UtcNow,
			};

			// select by serverId
			var serverResult = _connection.Query<Entities.Server>(
				"select * from [HangFire.Server] where Id = @id",
				new {id = serverId}).SingleOrDefault();

			if (serverResult == null)
			{
				// if not found insert
				_connection.Execute(
					"insert into [HangFire.Server] (Id, Data, LastHeartbeat) values (@id, @data, datetime('now', 'utc'))",
					new {id = serverId, data = JobHelper.ToJson(data)});
			}
			else
			{
				// if found, update data + heartbeart
				_connection.Execute(
					"update [HangFire.Server] set Data = @data, LastHeartbeat = datetime('now', 'utc') where Id = @id",
					new { id = serverId, data = JobHelper.ToJson(data) });
			}

			//_connection.Execute(
			//    @";merge [HangFire.Server] with (holdlock) as Target "
			//    + @"using (VALUES (@id, @data, @heartbeat)) as Source (Id, Data, Heartbeat) "  // << SOURCE
			//    + @"on Target.Id = Source.Id "
			//    + @"when matched then UPDATE set Data = Source.Data, LastHeartbeat = Source.Heartbeat "
			//    + @"when not matched then INSERT (Id, Data, LastHeartbeat) values (Source.Id, Source.Data, Source.Heartbeat);",
			//    new { id = serverId, data = JobHelper.ToJson(data), heartbeat = DateTime.UtcNow });
		}

		public override void RemoveServer(string serverId)
		{
			if (serverId == null) throw new ArgumentNullException("serverId");

			_connection.Execute(
				@"delete from [HangFire.Server] where Id = @id",
				new { id = serverId });
		}

		public override void Heartbeat(string serverId)
		{
			if (serverId == null) throw new ArgumentNullException("serverId");

			_connection.Execute(
				@"update [HangFire.Server] set LastHeartbeat = datetime('now', 'utc') where Id = @id",
				new { id = serverId });
		}

		public override int RemoveTimedOutServers(TimeSpan timeOut)
		{
			if (timeOut.Duration() != timeOut)
			{
				throw new ArgumentException("The `timeOut` value must be positive.", "timeOut");
			}

			return _connection.Execute(
				@"delete from [HangFire.Server] where LastHeartbeat < @timeOutAt",
				new { timeOutAt = DateTime.UtcNow.Add(timeOut.Negate()) });
		}

		public override long GetSetCount(string key)
		{
			if (key == null) throw new ArgumentNullException("key");

			return _connection.Query<int>(
				"select count([Key]) from [HangFire.Set] where [Key] = @key",
				new { key = key }).First();
		}

		public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt)
		{
			if (key == null) throw new ArgumentNullException("key");

			const string query = @"
select [Value] 
from [HangFire.Set]
where [Key] = @key 
order by Id asc
limit @limit offset @offset";

			return _connection
				.Query<string>(query, new { key = key, limit = endingAt - startingFrom + 1, offset = startingFrom })
				.ToList();
		}

		public override TimeSpan GetSetTtl(string key)
		{
			if (key == null) throw new ArgumentNullException("key");

			const string query = @"
select min([ExpireAt]) from [HangFire.Set]
where [Key] = @key";

			var result = _connection.Query<DateTime?>(query, new { key = key }).Single();
			if (!result.HasValue) return TimeSpan.FromSeconds(-1);

			return result.Value.ToLocalTime() - DateTime.UtcNow.ToLocalTime();
		}

		public override long GetCounter(string key)
		{
			if (key == null) throw new ArgumentNullException("key");

			const string query = @"
select sum(s.[Value]) from (select sum([Value]) as [Value] from [HangFire.Counter]
where [Key] = @key
union all
select [Value] from [HangFire.AggregatedCounter]
where [Key] = @key) as s";

			return _connection.Query<long?>(query, new { key = key }).Single() ?? 0;
		}

		public override long GetHashCount(string key)
		{
			if (key == null) throw new ArgumentNullException("key");

			const string query = @"
select count([Id]) from [HangFire.Hash]
where [Key] = @key";

			return _connection.Query<long>(query, new { key = key }).Single();
		}

		public override TimeSpan GetHashTtl(string key)
		{
			if (key == null) throw new ArgumentNullException("key");

			const string query = @"
select min([ExpireAt]) from [HangFire.Hash]
where [Key] = @key";

			var result = _connection.Query<DateTime?>(query, new { key = key }).Single();
			if (!result.HasValue) return TimeSpan.FromSeconds(-1);

			return result.Value.ToLocalTime() - DateTime.UtcNow.ToLocalTime();
		}

		public override string GetValueFromHash(string key, string name)
		{
			if (key == null) throw new ArgumentNullException("key");
			if (name == null) throw new ArgumentNullException("name");

			const string query = @"
select [Value] from [HangFire.Hash]
where [Key] = @key and [Field] = @field";

			return _connection.Query<string>(query, new { key = key, field = name }).SingleOrDefault();
		}

		public override long GetListCount(string key)
		{
			if (key == null) throw new ArgumentNullException("key");

			const string query = @"
select count([Id]) from [HangFire.List]
where [Key] = @key";

			return _connection.Query<long>(query, new { key = key }).Single();
		}

		public override TimeSpan GetListTtl(string key)
		{
			if (key == null) throw new ArgumentNullException("key");

			const string query = @"
select min([ExpireAt]) from [HangFire.List]
where [Key] = @key";

			var result = _connection.Query<DateTime?>(query, new { key = key }).Single();
			if (!result.HasValue) return TimeSpan.FromSeconds(-1);

			return result.Value.ToLocalTime() - DateTime.UtcNow.ToLocalTime();
		}

		public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt)
		{
			if (key == null) throw new ArgumentNullException("key");

			const string query = @"
	select [Value] 
	from [HangFire.List]
	where [Key] = @key 
	order by Id desc
	limit @limit offset @offset";

			return _connection
				.Query<string>(query, new { key = key, limit = endingAt - startingFrom + 1, offset = startingFrom })
				.ToList();
		}

		public override List<string> GetAllItemsFromList(string key)
		{
			if (key == null) throw new ArgumentNullException("key");

			const string query = @"
select [Value] from [HangFire.List]
where [Key] = @key
order by [Id] desc";

			return _connection.Query<string>(query, new { key = key }).ToList();
		}
	}
}

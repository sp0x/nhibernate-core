﻿using System;
using System.Data.Common;
using System.Data.Odbc;
using System.Data.SqlClient;
using System.Configuration;
using System.Threading;
using System.Transactions;
using NHibernate.Dialect;
using NHibernate.Driver;
using NHibernate.Engine;
using NUnit.Framework;

using Environment = NHibernate.Cfg.Environment;

namespace NHibernate.Test.NHSpecificTest.NH2420
{
	[TestFixture]
	public class Fixture : BugTestCase
	{
		public override string BugNumber
		{
			get { return "NH2420"; }
		}

		protected override bool AppliesTo(Dialect.Dialect dialect)
		{
			return (dialect is MsSql2005Dialect);
		}

		private string FetchConnectionStringFromConfiguration()
		{
			string connectionString;
			if (cfg.Properties.TryGetValue(Environment.ConnectionString, out connectionString))
			{
				Assert.That(connectionString, Is.Not.Null.Or.Empty);
				return connectionString;
			}
			string connectionStringName;
			if (cfg.Properties.TryGetValue(Environment.ConnectionStringName, out connectionStringName))
			{
				var connectionStringSettings = ConfigurationManager.ConnectionStrings[connectionStringName];
				Assert.That(connectionStringSettings, Is.Not.Null);
				connectionString = connectionStringSettings.ConnectionString;
				Assert.That(connectionString, Is.Not.Null.Or.Empty);
				return connectionString;
			}
			else
			{
				Assert.Fail("Unable to find a connection string or connection string name");
				return string.Empty;
			}
		}

		[Test]
		public void ShouldBeAbleToReleaseSuppliedConnectionAfterDistributedTransaction()
		{
			string connectionString = FetchConnectionStringFromConfiguration();
			ISession s;
			DbConnection connection = null;
			try
			{
				using (var ts = new TransactionScope())
				{
					// Enlisting DummyEnlistment as a durable resource manager will start
					// a DTC transaction
					System.Transactions.Transaction.Current.EnlistDurable(
						DummyEnlistment.Id,
						new DummyEnlistment(),
						EnlistmentOptions.None);

					if (sessions.ConnectionProvider.Driver.GetType() == typeof(OdbcDriver))
						connection = new OdbcConnection(connectionString);
					else
						connection = new SqlConnection(connectionString);

					connection.Open();
					using (s = Sfi.OpenSession(connection))
					{
						s.Save(new MyTable {String = "hello!"});
					}
					// The ts disposal may try to flush the session, which, depending on the native generator
					// implementation for current dialect, may have something to do and will then try to use
					// the supplied connection. dispose connection here => flaky test, failing for dialects
					// not mandating an immediate insert on native generator save.
					// Delaying the connection disposal to after ts disposal.

					ts.Complete();
				}
			}
			finally
			{
				connection?.Dispose();
			}

			// Here it appears the second phase of the 2PC is not guaranteed to be executed
			// before exiting transaction scope disposal... So long for the current pattern,
			// it looks doomed. Sleeping here allow to keep that test succeeding by letting
			// enough time for the second phase to disconnect the session.
			//Thread.Sleep(100);

			// Prior to the patch, an InvalidOperationException exception would occur in the
			// TransactionCompleted delegate at this point with the message, "Disconnect cannot
			// be called while a transaction is in progress". Although the exception can be
			// seen reported in the IDE, NUnit fails to see it. The TransactionCompleted event
			// fires *after* the transaction is committed and so it doesn't affect the success
			// of the transaction.
			Assert.That(s.IsConnected, Is.False);
			Assert.That(((ISessionImplementor)s).ConnectionManager.IsConnected, Is.False);
			Assert.That(((ISessionImplementor)s).IsClosed, Is.True);
		}

		protected override void OnTearDown()
		{
			using (ISession s = OpenSession())
			{
				s.Delete("from MyTable");
				s.Flush();
			}
		}
	}
}

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Shuttle.Core.Data;
using Shuttle.Core.Infrastructure;
using Shuttle.ESB.Core;

namespace Shuttle.ESB.SqlServer
{
	public class SubscriptionManager :
		ISubscriptionManager,
		IRequireInitialization
	{
		private readonly DataSource _subscriptionDataSource;

		private readonly List<string> deferredSubscriptions = new List<string>();

		private readonly IDatabaseGateway databaseGateway;
		private readonly IDatabaseConnectionFactory databaseConnectionFactory;
		private readonly IScriptProvider scriptProvider;

		private IServiceBusConfiguration serviceBusConfiguration;

		private readonly Dictionary<string, List<string>> subscribers = new Dictionary<string, List<string>>();

		private static readonly object padlock = new object();

		public static ISubscriptionManager Default()
		{
			var configuration = SqlServerSection.Configuration();

			return
				new SubscriptionManager(configuration,
										new ScriptProvider(configuration),
										DatabaseConnectionFactory.Default(),
										DatabaseGateway.Default());
		}


		public SubscriptionManager(ISqlServerConfiguration configuration, IScriptProvider scriptProvider,
								   IDatabaseConnectionFactory databaseConnectionFactory, IDatabaseGateway databaseGateway)
		{
			Guard.AgainstNull(configuration, "configuration");
			Guard.AgainstNull(scriptProvider, "scriptProvider");
			Guard.AgainstNull(databaseConnectionFactory, "databaseConnectionFactory");
			Guard.AgainstNull(databaseGateway, "databaseGateway");

			this.scriptProvider = scriptProvider;
			this.databaseConnectionFactory = databaseConnectionFactory;
			this.databaseGateway = databaseGateway;

			_subscriptionDataSource = new DataSource(configuration.SubscriptionManagerConnectionStringName, new SqlDbDataParameterFactory());
		}

		protected bool HasDeferredSubscriptions
		{
			get { return deferredSubscriptions.Count > 0; }
		}

		protected bool Started
		{
			get { return serviceBusConfiguration != null; }
		}

		public void Initialize(IServiceBus bus)
		{
			serviceBusConfiguration = bus.Configuration;

			using (databaseConnectionFactory.Create(_subscriptionDataSource))
			{
				if (databaseGateway.GetScalarUsing<int>(
					_subscriptionDataSource,
					RawQuery.Create(
						scriptProvider.GetScript(
							Script.SubscriptionManagerExists))) != 1)
				{
					throw new SubscriptionManagerException(SqlResources.SubscriptionManagerDatabaseNotConfigured);
				}
			}

			if (HasDeferredSubscriptions)
			{
				Subscribe(deferredSubscriptions);
			}
		}

		public void Subscribe(IEnumerable<string> messageTypeFullNames)
		{
			Guard.AgainstNull(messageTypeFullNames, "messageTypeFullNames");

			if (!Started)
			{
				deferredSubscriptions.AddRange(messageTypeFullNames);

				return;
			}

			using (databaseConnectionFactory.Create(_subscriptionDataSource))
			{
				foreach (var messageType in messageTypeFullNames)
				{
					databaseGateway.ExecuteUsing(
						_subscriptionDataSource,
						RawQuery.Create(
							scriptProvider.GetScript(Script.SubscriptionManagerSubscribe))
								.AddParameterValue(SubscriptionManagerColumns.InboxWorkQueueUri,
												   serviceBusConfiguration.Inbox.WorkQueue.Uri.ToString())
								.AddParameterValue(SubscriptionManagerColumns.MessageType, messageType));
				}
			}
		}

		public void Subscribe(string messageTypeFullName)
		{
			Subscribe(new[] { messageTypeFullName });
		}

		public void Subscribe(IEnumerable<Type> messageTypes)
		{
			Subscribe(messageTypes.Select(messageType => messageType.FullName).ToList());
		}

		public void Subscribe(Type messageType)
		{
			Subscribe(new[] { messageType.FullName });
		}

		public void Subscribe<T>()
		{
			Subscribe(new[] { typeof(T).FullName });
		}

        public IEnumerable<string> GetSubscribedUris(object message)
        {
            Guard.AgainstNull(message, "message");

            return GetSubscribedUris(message.GetType().FullName);
        }

        public IEnumerable<string> GetSubscribedUris(string messageType)
        {
            Guard.AgainstNullOrEmptyString(messageType, "messageType");
            
            if (!subscribers.ContainsKey(messageType))
            {
                lock (padlock)
                {
                    if (!subscribers.ContainsKey(messageType))
                    {
                        DataTable table;

                        using (databaseConnectionFactory.Create(_subscriptionDataSource))
                        {
                            table = databaseGateway.GetDataTableFor(
                                _subscriptionDataSource,
                                RawQuery.Create(
                                    scriptProvider.GetScript(
                                        Script.SubscriptionManagerInboxWorkQueueUris))
                                        .AddParameterValue(SubscriptionManagerColumns.MessageType, messageType));
                        }

                        subscribers.Add(messageType, (from DataRow row in table.Rows
                                                      select SubscriptionManagerColumns.InboxWorkQueueUri.MapFrom(row))
                                                         .ToList());
                    }
                }
            }

            return subscribers[messageType];
        }
	}
}
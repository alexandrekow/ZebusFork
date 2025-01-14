﻿using System;
using System.Configuration;
using System.IO;
using System.Threading;
using Abc.Zebus.Core;
using Abc.Zebus.Directory;
using Abc.Zebus.Dispatch;
using Abc.Zebus.Log4Net;
using Abc.Zebus.Monitoring;
using Abc.Zebus.Persistence.Cassandra;
using Abc.Zebus.Persistence.Cassandra.Cql;
using Abc.Zebus.Persistence.Cassandra.PeriodicAction;
using Abc.Zebus.Persistence.Initialization;
using Abc.Zebus.Persistence.Matching;
using Abc.Zebus.Persistence.Reporter;
using Abc.Zebus.Persistence.RocksDb;
using Abc.Zebus.Persistence.Storage;
using Abc.Zebus.Persistence.Transport;
using Abc.Zebus.Transport;
using log4net;
using log4net.Config;
using log4net.Core;
using Microsoft.Extensions.Logging;
using StructureMap;
using ILogger = Microsoft.Extensions.Logging.ILogger;

#nullable enable

namespace Abc.Zebus.Persistence.Runner
{
    internal class Program
    {
        private static readonly ManualResetEvent _cancelKeySignal = new ManualResetEvent(false);
        private static readonly ILogger _log = ZebusLogManager.GetLogger(typeof(Program));

        public static void Main()
        {
            ZebusLogManager.LoggerFactory = new Log4NetFactory();

            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                _cancelKeySignal.Set();
            };

            XmlConfigurator.ConfigureAndWatch(LoggerManager.GetRepository(typeof(Program).Assembly), new FileInfo(InBaseDirectory("log4net.config")));
            _log.LogInformation("Starting persistence");

            var appSettingsConfiguration = new AppSettingsConfiguration();
            var useCassandraStorage = ConfigurationManager.AppSettings["PersistenceStorage"] == "Cassandra";
            var busFactory = new BusFactory().WithConfiguration(appSettingsConfiguration, ConfigurationManager.AppSettings["Environment"]!)
                                             .WithScan()
                                             .WithEndpoint(ConfigurationManager.AppSettings["Endpoint"]!)
                                             .WithPeerId(ConfigurationManager.AppSettings["PeerId"]!);

            InjectPersistenceServiceSpecificConfiguration(busFactory, appSettingsConfiguration, useCassandraStorage);

            using (busFactory.CreateAndStartBus())
            {
                _log.LogInformation("Starting initialisers");
                var inMemoryMessageMatcherInitializer = busFactory.Container.GetInstance<InMemoryMessageMatcherInitializer>();
                inMemoryMessageMatcherInitializer.BeforeStart();

                OldestNonAckedMessageUpdaterPeriodicAction? oldestNonAckedMessageUpdaterPeriodicAction = null;
                if (useCassandraStorage)
                {
                    oldestNonAckedMessageUpdaterPeriodicAction = busFactory.Container.GetInstance<OldestNonAckedMessageUpdaterPeriodicAction>();
                    oldestNonAckedMessageUpdaterPeriodicAction.AfterStart();
                }

                _log.LogInformation("Persistence started");

                _cancelKeySignal.WaitOne();

                _log.LogInformation("Stopping initialisers");
                oldestNonAckedMessageUpdaterPeriodicAction?.BeforeStop();

                var messageReplayerInitializer = busFactory.Container.GetInstance<MessageReplayerInitializer>();
                messageReplayerInitializer.BeforeStop();

                inMemoryMessageMatcherInitializer.AfterStop();

                _log.LogInformation("Persistence stopped");
            }
        }

        private static string InBaseDirectory(string path)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, path);
        }

        private static void InjectPersistenceServiceSpecificConfiguration(BusFactory busFactory, AppSettingsConfiguration configuration, bool useCassandraStorage)
        {
            busFactory.ConfigureContainer(c =>
            {
                c.ForSingletonOf<IPersistenceConfiguration>().Use(configuration);

                if (useCassandraStorage)
                {
                    _log.LogInformation("Using Cassandra storage implementation");
                    c.ForSingletonOf<IStorage>().Use<CqlStorage>();
                }
                else
                {
                    _log.LogInformation("Using RocksDB storage implementation");
                    c.ForSingletonOf<IStorage>().Use<RocksDbStorage>();
                }

                c.ForSingletonOf<IMessageReplayerRepository>().Use<MessageReplayerRepository>();
                c.ForSingletonOf<IMessageReplayer>().Use<MessageReplayer>();

                c.ForSingletonOf<IMessageDispatcher>().Use(typeof(Func<IContext, MessageDispatcher>).Name,
                                                           ctx =>
                                                           {
                                                               var dispatcher = ctx.GetInstance<MessageDispatcher>();
                                                               dispatcher.ConfigureHandlerFilter(x => x != typeof(PeerDirectoryClient));

                                                               return dispatcher;
                                                           });

                c.ForSingletonOf<ITransport>().Use<QueueingTransport>().Ctor<ITransport>().Is<ZmqTransport>();
                c.ForSingletonOf<IInMemoryMessageMatcher>().Use<InMemoryMessageMatcher>();
                c.Forward<IInMemoryMessageMatcher, IProvideQueueLength>();
                c.ForSingletonOf<IStoppingStrategy>().Use<PersistenceStoppingStrategy>();

                c.ForSingletonOf<IReporter>().Use<NoopReporter>();

                // Cassandra specific
                if (useCassandraStorage)
                {
                    c.ForSingletonOf<ICqlStorage>().Use<CqlStorage>();
                    c.ForSingletonOf<CassandraCqlSessionManager>().Use(() => CassandraCqlSessionManager.Create());
                    c.ForSingletonOf<ICqlPersistenceConfiguration>().Use<CassandraAppSettingsConfiguration>();
                }
            });
        }
    }
}

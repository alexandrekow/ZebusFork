﻿using System;
using Abc.Zebus.Persistence.Cassandra.Cql;

namespace Abc.Zebus.Persistence.Cassandra
{
    public interface ICqlPersistenceConfiguration : ICassandraConfiguration
    {
        TimeSpan OldestMessagePerPeerCheckPeriod { get; set; }
        TimeSpan OldestMessagePerPeerGlobalCheckPeriod { get; set; }
    }
}

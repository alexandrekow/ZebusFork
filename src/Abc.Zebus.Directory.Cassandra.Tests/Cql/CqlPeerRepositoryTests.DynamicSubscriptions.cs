﻿using System;
using System.Linq;
using Abc.Zebus.Directory.Tests;
using Abc.Zebus.Routing;
using Abc.Zebus.Testing;
using Abc.Zebus.Testing.Extensions;
using Abc.Zebus.Util;
using Cassandra;
using Cassandra.Data.Linq;
using NUnit.Framework;

namespace Abc.Zebus.Directory.Cassandra.Tests.Cql
{
    public partial class CqlPeerRepositoryTests
    {
        [Test]
        public void should_add_dynamic_subscriptions()
        {
            var peerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
            _repository.AddOrUpdatePeer(peerDescriptor);

            _repository.AddDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<int>() });

            var fetched = _repository.Get(peerDescriptor.Peer.Id);
            fetched.Subscriptions.ShouldEqual(new[]
            {
                CreateSubscriptionFor<FakeCommand>(),
                CreateSubscriptionFor<int>()
            });
        }

        private Subscription CreateSubscriptionFor<TMessage>(params string[] bindingKeyParts)
        {
            return new Subscription(MessageUtil.GetTypeId(typeof(TMessage)), new BindingKey(bindingKeyParts));
        }

        private SubscriptionsForType CreateSubscriptionsForType<TMessage>(params BindingKey[] bindings)
        {
            return new SubscriptionsForType(MessageUtil.GetTypeId(typeof(TMessage)), bindings.Any() ? bindings : new[] { BindingKey.Empty });
        }

        [Test]
        public void should_remove_dynamic_subscriptions()
        {
            var peerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
            _repository.AddOrUpdatePeer(peerDescriptor);
            _repository.AddDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<int>() });

            _repository.RemoveDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { MessageUtil.GetTypeId(typeof(int)) });

            var fetched = _repository.Get(peerDescriptor.Peer.Id);
            fetched.ShouldHaveSamePropertiesAs(peerDescriptor);
        }

        [Test]
        public void should_remove_the_dynamic_subscriptions_of_a_peer_when_removing_it()
        {
            var peerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
            _repository.AddOrUpdatePeer(peerDescriptor);
            _repository.AddDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<int>() });

			_repository.RemovePeer(peerDescriptor.PeerId);

            _repository.Get(peerDescriptor.PeerId).ShouldBeNull();

            var retrievedSubscriptions = DataContext.DynamicSubscriptions
                                                    .SetConsistencyLevel(ConsistencyLevel.LocalQuorum)
                                                    .Where(s => s.PeerId == peerDescriptor.PeerId.ToString())
                                                    .Execute();
			retrievedSubscriptions.ShouldBeEmpty();
        }

        [Test]
		public void should_not_add_dynamic_subscriptions_using_outdated_add_command()
        {
            var pastPeerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
			pastPeerDescriptor.TimestampUtc = SystemDateTime.UtcNow.AddMinutes(-1).RoundToMillisecond();
            var presentPeerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
			_repository.AddOrUpdatePeer(presentPeerDescriptor);

            _repository.RemoveDynamicSubscriptionsForTypes(presentPeerDescriptor.PeerId, presentPeerDescriptor.TimestampUtc.Value, new[] { MessageUtil.GetTypeId(typeof(int)) });
            _repository.AddDynamicSubscriptionsForTypes(pastPeerDescriptor.PeerId, pastPeerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<int>() });

            var fetched = _repository.Get(presentPeerDescriptor.Peer.Id);
            fetched.Subscriptions.ShouldEqual(new[] { CreateSubscriptionFor<FakeCommand>() });
        }

        [Test]
        public void should_not_remove_dynamic_subscriptions_using_outdated_delete_command()
        {
            var pastPeerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
            pastPeerDescriptor.TimestampUtc = SystemDateTime.UtcNow.AddMinutes(-1).RoundToMillisecond();
            var presentPeerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
            _repository.AddOrUpdatePeer(presentPeerDescriptor);
            _repository.AddDynamicSubscriptionsForTypes(presentPeerDescriptor.PeerId, presentPeerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<int>() });

            _repository.AddDynamicSubscriptionsForTypes(presentPeerDescriptor.PeerId, presentPeerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<int>() });
            _repository.RemoveDynamicSubscriptionsForTypes(pastPeerDescriptor.PeerId, pastPeerDescriptor.TimestampUtc.Value, new[] { MessageUtil.GetTypeId(typeof(int)) });

            var fetched = _repository.Get(presentPeerDescriptor.Peer.Id);
            fetched.Subscriptions.ShouldEqual(new[]
            {
                CreateSubscriptionFor<FakeCommand>(),
                CreateSubscriptionFor<int>()
            });
        }

        [Test]
        public void should_not_use_local_time_to_remove_dynamic_subscriptions()
        {
            var utcDateInThePast = DateTime.Now.AddDays(-1).ToUniversalTime();
            var peerInThePast = _peer1.ToPeerDescriptor(true, typeof(FakeCommand));
            peerInThePast.TimestampUtc = utcDateInThePast.RoundToMillisecond();

            _repository.RemoveAllDynamicSubscriptionsForPeer(_peer1.Id, peerInThePast.TimestampUtc.Value);
            _repository.AddOrUpdatePeer(peerInThePast);
            _repository.AddDynamicSubscriptionsForTypes(peerInThePast.PeerId, peerInThePast.TimestampUtc.Value.AddMilliseconds(1), new[] { CreateSubscriptionsForType<FakeCommand>(new BindingKey("toto")) });

            var retrievedPeer = _repository.GetPeers().ExpectedSingle();
            retrievedPeer.Subscriptions.Length.ShouldEqual(2);
        }

        [Test]
        public void should_get_a_peer_with_no_subscriptions_using_GetPeers()
        {
            var peerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true);

            _repository.AddOrUpdatePeer(peerDescriptor);

            var peerFetched = _repository.GetPeers().ExpectedSingle();
            peerFetched.ShouldHaveSamePropertiesAs(peerDescriptor);
        }

        [Test]
        public void parts_should_stay_in_order()
        {
            var peerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true);
            var random = new Random();
            var bindingKeyTokens = Enumerable.Range(1, 100).Select(i => random.Next().ToString()).ToArray();
            _repository.AddOrUpdatePeer(peerDescriptor);

            _repository.AddDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<int>(new BindingKey(bindingKeyTokens)) });

            var fetched = _repository.Get(peerDescriptor.Peer.Id);
            fetched.Subscriptions.ShouldEqual(new[] { CreateSubscriptionFor<int>(bindingKeyTokens) });
        }

        [Test]
        public void removing_a_dynamic_subscription_doesnt_remove_static_subscription()
        {
            var peerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
            _repository.AddOrUpdatePeer(peerDescriptor);

            _repository.RemoveDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { MessageUtil.GetTypeId(typeof(FakeCommand)) });

            var fetched = _repository.Get(peerDescriptor.Peer.Id);
            fetched.ShouldHaveSamePropertiesAs(peerDescriptor);
        }

        [Test]
        public void should_deduplicate_dynamic_subscriptions_and_static_subscriptions()
        {
            var peerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
            _repository.AddOrUpdatePeer(peerDescriptor);

            _repository.AddDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<FakeCommand>(), CreateSubscriptionsForType<FakeCommand>() });

            var fetched = _repository.Get(peerDescriptor.Peer.Id);
            fetched.Subscriptions.ShouldEqual(new[] { CreateSubscriptionFor<FakeCommand>() });
        }

		[Test]
        public void should_not_mixup_subscriptions_to_same_type_with_different_tokens()
        {
            var peerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
            _repository.AddOrUpdatePeer(peerDescriptor);

            _repository.AddDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<FakeCommand>(new BindingKey("bli"), new BindingKey("bla")) });

            var fetched = _repository.Get(peerDescriptor.Peer.Id);
            fetched.Subscriptions.ShouldEqual(new[]
            {
                CreateSubscriptionFor<FakeCommand>(),
                CreateSubscriptionFor<FakeCommand>("bli"),
                CreateSubscriptionFor<FakeCommand>("bla")
            });
        }

        [Test]
        public void should_not_erase_the_dynamic_subscriptions_of_a_peer_on_update()
        {
            var peerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
            _repository.AddDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<int>() });

            _repository.AddOrUpdatePeer(peerDescriptor);

            var expectedPeerDescriptor = new PeerDescriptor(peerDescriptor);
            expectedPeerDescriptor.Subscriptions = new[] { CreateSubscriptionFor<FakeCommand>(), CreateSubscriptionFor<int>() };
            var peerFetched = _repository.Get(peerDescriptor.Peer.Id);
            peerFetched.ShouldHaveSamePropertiesAs(expectedPeerDescriptor);
        }

        [Test]
        public void should_get_dynamic_subscriptions_in_GetPeers()
        {
            var peerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
            _repository.AddOrUpdatePeer(peerDescriptor);

            _repository.AddDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<int>() });

            var fetched = _repository.GetPeers().ExpectedSingle();
            fetched.Subscriptions.ShouldEqual(new[]
            {
                CreateSubscriptionFor<FakeCommand>(),
                CreateSubscriptionFor<int>()
            });
        }

        [Test]
        public void should_not_get_dynamic_subscriptions_in_GetPeers_if_called_with_proper_flag()
        {
            var peerDescriptor = _peer1.ToPeerDescriptorWithRoundedTime(true, typeof(FakeCommand));
            _repository.AddOrUpdatePeer(peerDescriptor);

            _repository.AddDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<int>() });

            var fetched = _repository.GetPeers(loadDynamicSubscriptions: false).ExpectedSingle();
            fetched.Subscriptions.ShouldEqual(new[]
            {
                CreateSubscriptionFor<FakeCommand>()
            });
        }

        [Test]
        public void should_handle_dynamic_subscriptions_with_empty_binding_key_beside_static_one()
        {
            var peerDescriptor = _peer1.ToPeerDescriptor(true, typeof(FakeCommand));
            _repository.AddOrUpdatePeer(peerDescriptor);

            _repository.AddDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<FakeCommand>() });

            var fetched = _repository.Get(peerDescriptor.Peer.Id);
            fetched.Subscriptions.ShouldEqual(new[]
            {
                CreateSubscriptionFor<FakeCommand>(),
            });
        }

        [Test]
        public void should_handle_dynamic_subscriptions_with_empty_binding_key_beside_specific_one()
        {
            var peerDescriptor = _peer1.ToPeerDescriptor(true, typeof(FakeCommand));
            _repository.AddOrUpdatePeer(peerDescriptor);

            _repository.AddDynamicSubscriptionsForTypes(peerDescriptor.PeerId, peerDescriptor.TimestampUtc.Value, new[] { CreateSubscriptionsForType<FakeCommand>(BindingKey.Empty, new BindingKey("toto")) });

            var fetched = _repository.Get(peerDescriptor.Peer.Id);
            fetched.Subscriptions.ShouldEqual(new[]
            {
                CreateSubscriptionFor<FakeCommand>(),
                CreateSubscriptionFor<FakeCommand>("toto"),
            });
        }

        [Test]
        public void should_remove_dynamic_subscriptions_for_peer()
        {
            var firstPeer = _peer1.ToPeerDescriptor(true, typeof(FakeCommand));
            var secondPeer = _peer2.ToPeerDescriptor(true, typeof(FakeCommand));
            _repository.AddOrUpdatePeer(firstPeer);
            _repository.AddOrUpdatePeer(secondPeer);
            _repository.AddDynamicSubscriptionsForTypes(firstPeer.PeerId, secondPeer.TimestampUtc.Value, new[] { CreateSubscriptionsForType<FakeCommand>(new BindingKey("toto")) });
            _repository.AddDynamicSubscriptionsForTypes(secondPeer.PeerId, secondPeer.TimestampUtc.Value, new[] { CreateSubscriptionsForType<FakeCommand>(new BindingKey("toto")) });

            _repository.RemoveAllDynamicSubscriptionsForPeer(secondPeer.PeerId, secondPeer.TimestampUtc.Value.AddMilliseconds(1));

            var fetched = _repository.GetPeers().ToList();
            fetched.Count.ShouldEqual(2);
            var untouchedPeer = fetched.Single(peer => peer.PeerId == firstPeer.PeerId);
            var peerWithTruncatedSubscriptions = fetched.Single(peer => peer.PeerId == secondPeer.PeerId);
            untouchedPeer.Subscriptions.ShouldEqual(new[]
            {
                CreateSubscriptionFor<FakeCommand>(),
                CreateSubscriptionFor<FakeCommand>("toto")
            });
            peerWithTruncatedSubscriptions.Subscriptions.ShouldEqual(new[]
            {
                CreateSubscriptionFor<FakeCommand>()
            });
        }

        [Test]
        public void should_use_specified_timestamp_to_remove_dynamic_subscriptions()
        {
            var peer = _peer1.ToPeerDescriptor(true);
            var subscriptionsForType = CreateSubscriptionsForType<FakeCommand>(new BindingKey("X"));

            _repository.AddOrUpdatePeer(peer);
            _repository.AddDynamicSubscriptionsForTypes(peer.PeerId, peer.TimestampUtc.Value.AddMilliseconds(1), new[] { subscriptionsForType });

            using (SystemDateTime.Set(utcNow: DateTime.UtcNow.AddHours(1)))
            {
                _repository.RemoveAllDynamicSubscriptionsForPeer(peer.PeerId, peer.TimestampUtc.Value.AddMilliseconds(2));
            }

            _repository.AddDynamicSubscriptionsForTypes(peer.PeerId, peer.TimestampUtc.Value.AddMilliseconds(3), new[] { subscriptionsForType });

            var fetched = _repository.GetPeers().ExpectedSingle();
            fetched.Subscriptions.ShouldNotBeEmpty();
        }
    }
}

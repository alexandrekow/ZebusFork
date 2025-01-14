﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Abc.Zebus.Directory.Configuration;
using Abc.Zebus.Directory.Storage;
using Abc.Zebus.Routing;
using Abc.Zebus.Testing;
using Abc.Zebus.Testing.Comparison;
using Abc.Zebus.Testing.Extensions;
using Abc.Zebus.Util;
using Moq;
using NUnit.Framework;

namespace Abc.Zebus.Directory.Tests
{
    [TestFixture]
    public class PeerDirectoryServerTests
    {
        private PeerDirectoryServer _peerDirectory;
        private Mock<IPeerRepository> _repositoryMock;
        private TestBus _bus;
        private UpdatedPeer _updatedPeer;
        private Peer _self;
        private Peer _otherPeer;
        private bool _disableDynamicSubscriptionsForDirectoryOutgoingMessages;

        [SetUp]
        public void Setup()
        {
            _repositoryMock = new Mock<IPeerRepository>();
            var configurationMock = new Mock<IDirectoryConfiguration>();
            configurationMock.SetupGet(conf => conf.DisableDynamicSubscriptionsForDirectoryOutgoingMessages).Returns(() => _disableDynamicSubscriptionsForDirectoryOutgoingMessages);
            _peerDirectory = new PeerDirectoryServer(configurationMock.Object, _repositoryMock.Object);

            _updatedPeer = null;
            _peerDirectory.PeerUpdated += (id, action) => _updatedPeer = new UpdatedPeer(id, action);

            _bus = new TestBus();

            _self = new Peer(new PeerId("Abc.DirectoryService.0"), "tcp://abc:42");
            _otherPeer = new Peer(new PeerId("Abc.Testing.0"), "tcp://abc:123");
        }

        [Test]
        public async Task register_persist_state_and_advertise()
        {
            using (SystemDateTime.PauseTime())
            using (SystemDateTime.Set(SystemDateTime.Now))
            {
                var peerDescriptor = _self.ToPeerDescriptor(false, typeof(FakeCommand));

                await _peerDirectory.RegisterAsync(_bus, peerDescriptor.Peer, peerDescriptor.Subscriptions);

                _bus.ExpectExactly(new PeerStarted(peerDescriptor));
                _repositoryMock.Verify(x => x.AddOrUpdatePeer(It.Is<PeerDescriptor>(descriptor => peerDescriptor.DeepCompare(descriptor))));
            }
        }

        [Test]
        public async Task should_raise_registered_event()
        {
            var peerDescriptor = _self.ToPeerDescriptor(true, typeof(FakeCommand));

            var raised = false;
            _peerDirectory.Registered += () => raised = true;

            await _peerDirectory.RegisterAsync(_bus, peerDescriptor.Peer, new Subscription[0]);

            raised.ShouldBeTrue();
        }

        [Test]
        public async Task unregister_should_persist_state_and_advertise()
        {
            using (SystemDateTime.PauseTime())
            using (SystemDateTime.Set(SystemDateTime.Now))
            {
                var peerDescriptor = _self.ToPeerDescriptor(true, typeof(FakeCommand));

                _repositoryMock.Setup(repo => repo.Get(It.Is<PeerId>(id => peerDescriptor.Peer.Id.Equals(id)))).Returns(peerDescriptor);
                await _peerDirectory.RegisterAsync(_bus, peerDescriptor.Peer, peerDescriptor.Subscriptions);

                await _peerDirectory.UnregisterAsync(_bus);

                _bus.Expect(new PeerStopped(peerDescriptor.Peer));
                _repositoryMock.Verify(repo => repo.Get(It.Is<PeerId>(id => peerDescriptor.Peer.Id.Equals(id))));
                _repositoryMock.Verify(repo => repo.AddOrUpdatePeer(It.Is<PeerDescriptor>(descriptor => peerDescriptor.DeepCompare(descriptor))));
            }
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public void should_retrieve_the_peers_from_the_repository(bool disableDynamicSubscriptions)
        {
            _disableDynamicSubscriptionsForDirectoryOutgoingMessages = disableDynamicSubscriptions;
            _repositoryMock.Setup(repo => repo.GetPeers(It.Is<bool>(loadDynamicSubs => loadDynamicSubs != disableDynamicSubscriptions)))
                           .Returns(new[]
                           {
                               TestData.PersistentPeerDescriptor("tcp://goodapple:123", typeof(FakeCommand)),
                               TestData.PersistentPeerDescriptor("tcp://badapple:123", typeof(string))
                           });

            var peersHandlingFakeCommand = _peerDirectory.GetPeersHandlingMessage(new FakeCommand()).ToList();

            peersHandlingFakeCommand.Count().ShouldEqual(1);
            peersHandlingFakeCommand.First().EndPoint.ShouldEqual("tcp://goodapple:123");
        }

        [Test]
        public void should_get_peer_with_matching_subscrition_binding_key()
        {
            var command = new FakeRoutableCommand(10, "u.name");

            var peerDescriptor = TestData.PersistentPeerDescriptor("tcp://goodapple:123", new Subscription(command.TypeId(), new BindingKey("10", "#")));
            _repositoryMock.Setup(x => x.GetPeers(It.IsAny<bool>())).Returns(new[] { peerDescriptor });

            var peer = _peerDirectory.GetPeersHandlingMessage(command).Single();
            peer.Id.ShouldEqual(peerDescriptor.Peer.Id);
        }

        [Test]
        public void should_not_get_peer_with_non_matching_subscrition_binding_key()
        {
            var command = new FakeRoutableCommand(10, "u.name");

            var peerDescriptor = TestData.PersistentPeerDescriptor("tcp://goodapple:123", new Subscription(command.TypeId(), new BindingKey("12", "#")));
            _repositoryMock.Setup(x => x.GetPeers(It.IsAny<bool>())).Returns(new[] { peerDescriptor });

            _peerDirectory.GetPeersHandlingMessage(command).ShouldBeEmpty();
        }

        [Test]
        public void should_get_peer_by_id()
        {
            var peerDescriptor = TestData.PersistentPeerDescriptor("tcp://badapple:123", typeof(string));
            _repositoryMock.Setup(repo => repo.Get(peerDescriptor.Peer.Id)).Returns(peerDescriptor);

            var fetchedPeerDescriptor = _peerDirectory.GetPeerDescriptor(peerDescriptor.PeerId);
            var fetchedPeer = _peerDirectory.GetPeer(peerDescriptor.PeerId);

            fetchedPeerDescriptor.ShouldEqualDeeply(peerDescriptor);
            fetchedPeer.ShouldEqualDeeply(peerDescriptor.Peer);
        }

        [Test]
        public void should_get_all_peers()
        {
            var peerDescriptor = TestData.PersistentPeerDescriptor("tcp://badapple:123", typeof(string));
            _repositoryMock.Setup(repo => repo.GetPeers(It.IsAny<bool>())).Returns(new[] { peerDescriptor });

            var fetchedPeerDescriptor = _peerDirectory.GetPeerDescriptors();

            fetchedPeerDescriptor.Single().ShouldEqualDeeply(peerDescriptor);
        }

        [Test]
        public async Task should_publish_event_when_modifying_subscriptions()
        {
            using (SystemDateTime.PauseTime())
            {
                var subscriptionsForTypes = new[] { new SubscriptionsForType(MessageUtil.GetTypeId(typeof(int)), BindingKey.Empty) };
                await _peerDirectory.RegisterAsync(_bus, _self, new[] { Subscription.Any<FakeCommand>() });
                _bus.ClearMessages();

                await _peerDirectory.UpdateSubscriptionsAsync(_bus, subscriptionsForTypes);

                _bus.ExpectExactly(new PeerSubscriptionsForTypesUpdated(_self.Id, SystemDateTime.UtcNow, subscriptionsForTypes));
            }
        }

        [Test]
        public async Task should_specify_datetime_kind_when_adding_subscriptions_for_a_type()
        {
            var subscriptionsForTypes = new[] { new SubscriptionsForType(MessageUtil.GetTypeId(typeof(int)), BindingKey.Empty) };
            await _peerDirectory.RegisterAsync(_bus, _self, new[] { Subscription.Any<FakeCommand>() });

            await _peerDirectory.UpdateSubscriptionsAsync(_bus, subscriptionsForTypes);

            _repositoryMock.Verify(repo => repo.AddDynamicSubscriptionsForTypes(_self.Id, It.Is<DateTime>(date => date.Kind == DateTimeKind.Utc), subscriptionsForTypes));
        }

        [Test]
        public async Task should_specify_datetime_kind_when_removing_subscriptions_for_a_type()
        {
            var subscriptionsForTypes = new[] { new SubscriptionsForType(MessageUtil.GetTypeId(typeof(int))) };
            await _peerDirectory.RegisterAsync(_bus, _self, new[] { Subscription.Any<FakeCommand>() });

            await _peerDirectory.UpdateSubscriptionsAsync(_bus, subscriptionsForTypes);

            _repositoryMock.Verify(repo => repo.RemoveDynamicSubscriptionsForTypes(_self.Id, It.Is<DateTime>(date => date.Kind == DateTimeKind.Utc), new[] { MessageUtil.GetTypeId(typeof(int)) }));
        }

        [Test]
        public async Task should_handle_null_bindingkeys_array_when_removing_subscriptions()
        {
            using (SystemDateTime.PauseTime())
            {
                var subscriptionsForTypes = new[] { new SubscriptionsForType(MessageUtil.GetTypeId(typeof(int)), null) };
                await _peerDirectory.RegisterAsync(_bus, _self, new[] { Subscription.Any<FakeCommand>() });

                await _peerDirectory.UpdateSubscriptionsAsync(_bus, subscriptionsForTypes);

                _repositoryMock.Verify(repo => repo.RemoveDynamicSubscriptionsForTypes(_self.Id, SystemDateTime.UtcNow, new[] { MessageUtil.GetTypeId(typeof(int)) }));
            }
        }

        [Test]
        public async Task should_remove_peer_subscriptions_for_a_type_if_there_are_no_binding_keys()
        {
            using (SystemDateTime.PauseTime())
            {
                var subscriptionsForTypes = new[]
                {
                    new SubscriptionsForType(MessageUtil.GetTypeId(typeof(int))),
                    new SubscriptionsForType(MessageUtil.GetTypeId(typeof(double)), BindingKey.Empty)
                };
                await _peerDirectory.RegisterAsync(_bus, _self, Enumerable.Empty<Subscription>());
                _bus.ClearMessages();
                SubscriptionsForType[] addedSubscriptions = null;
                MessageTypeId[] removedMessageTypeIds = null;
                _repositoryMock.Setup(repo => repo.AddDynamicSubscriptionsForTypes(_self.Id, SystemDateTime.UtcNow, It.IsAny<SubscriptionsForType[]>()))
                               .Callback((PeerId peerId, DateTime timestampUtc, SubscriptionsForType[] subs) => addedSubscriptions = subs);
                _repositoryMock.Setup(repo => repo.RemoveDynamicSubscriptionsForTypes(_self.Id, SystemDateTime.UtcNow, It.IsAny<MessageTypeId[]>()))
                               .Callback((PeerId peerId, DateTime timestampUtc, MessageTypeId[] ids) => removedMessageTypeIds = ids);

                await _peerDirectory.UpdateSubscriptionsAsync(_bus, subscriptionsForTypes);

                var addedSubscription = addedSubscriptions.ExpectedSingle();
                addedSubscription.ShouldHaveSamePropertiesAs(new SubscriptionsForType(MessageUtil.GetTypeId(typeof(double)), BindingKey.Empty));
                var removedMessageTypeId = removedMessageTypeIds.ExpectedSingle();
                removedMessageTypeId.ShouldHaveSamePropertiesAs(MessageUtil.GetTypeId(typeof(int)));
                _bus.ExpectExactly(new PeerSubscriptionsForTypesUpdated(_self.Id, SystemDateTime.UtcNow, subscriptionsForTypes));
            }
        }

        [Test]
        public void should_raise_peer_updated_after_peer_started()
        {
            _peerDirectory.Handle(new PeerStarted(_otherPeer.ToPeerDescriptor(true)));

            _updatedPeer.ShouldNotBeNull();
            _updatedPeer.PeerId.ShouldEqual(_otherPeer.Id);
            _updatedPeer.Action.ShouldEqual(PeerUpdateAction.Started);
        }

        [Test]
        public void should_raise_peer_updated_after_peer_stopped()
        {
            _peerDirectory.Handle(new PeerStopped(_otherPeer));

            _updatedPeer.ShouldNotBeNull();
            _updatedPeer.PeerId.ShouldEqual(_otherPeer.Id);
            _updatedPeer.Action.ShouldEqual(PeerUpdateAction.Stopped);
        }

        [Test]
        public void should_raise_peer_updated_after_peer_decommissioned()
        {
            _peerDirectory.Handle(new PeerDecommissioned(_otherPeer.Id));

            _updatedPeer.ShouldNotBeNull();
            _updatedPeer.PeerId.ShouldEqual(_otherPeer.Id);
            _updatedPeer.Action.ShouldEqual(PeerUpdateAction.Decommissioned);
        }

        [Test]
        public void should_raise_peer_updated_after_peer_subscription_updated()
        {
            _peerDirectory.Handle(new PeerSubscriptionsUpdated(_otherPeer.ToPeerDescriptor(true)));

            _updatedPeer.ShouldNotBeNull();
            _updatedPeer.PeerId.ShouldEqual(_otherPeer.Id);
            _updatedPeer.Action.ShouldEqual(PeerUpdateAction.Updated);
        }

        [Test]
        public void should_raise_peer_updated_after_peer_not_responding()
        {
            _peerDirectory.Handle(new PeerNotResponding(_otherPeer.Id));

            _updatedPeer.ShouldNotBeNull();
            _updatedPeer.PeerId.ShouldEqual(_otherPeer.Id);
            _updatedPeer.Action.ShouldEqual(PeerUpdateAction.Updated);
        }

        [Test]
        public void should_raise_peer_updated_after_peer_responding()
        {
            _peerDirectory.Handle(new PeerResponding(_otherPeer.Id));

            _updatedPeer.ShouldNotBeNull();
            _updatedPeer.PeerId.ShouldEqual(_otherPeer.Id);
            _updatedPeer.Action.ShouldEqual(PeerUpdateAction.Updated);
        }

        [Test]
        public async Task should_measure_time_elapsed_since_last_directory_ping()
        {
            _peerDirectory.TimeSinceLastPing.ShouldEqual(TimeSpan.MaxValue);

            _bus.AddHandler<RegisterPeerCommand>(x => new RegisterPeerResponse(Array.Empty<PeerDescriptor>()));

            await _peerDirectory.RegisterAsync(_bus, _self, Enumerable.Empty<Subscription>());
            _peerDirectory.TimeSinceLastPing.ShouldBeGreaterOrEqualThan(TimeSpan.Zero);
            _peerDirectory.TimeSinceLastPing.ShouldNotEqual(TimeSpan.MaxValue);

            var time = _peerDirectory.TimeSinceLastPing;

            await Task.Delay(200.Milliseconds());

            _peerDirectory.TimeSinceLastPing.ShouldBeGreaterOrEqualThan(time + 180.Milliseconds());

            _peerDirectory.Handle(new PingPeerCommand());
            _peerDirectory.TimeSinceLastPing.ShouldBeLessThan(time);

            await _peerDirectory.UnregisterAsync(_bus);

            _peerDirectory.TimeSinceLastPing.ShouldEqual(TimeSpan.MaxValue);
        }

        private class UpdatedPeer
        {
            public readonly PeerId PeerId;
            public readonly PeerUpdateAction Action;

            public UpdatedPeer(PeerId peerId, PeerUpdateAction action)
            {
                PeerId = peerId;
                Action = action;
            }
        }
    }
}

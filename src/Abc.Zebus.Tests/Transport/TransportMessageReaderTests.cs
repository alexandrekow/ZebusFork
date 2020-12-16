﻿using System.Collections.Generic;
using System.IO;
using System.Text;
using Abc.Zebus.Serialization.Protobuf;
using Abc.Zebus.Testing;
using Abc.Zebus.Testing.Extensions;
using Abc.Zebus.Testing.Measurements;
using Abc.Zebus.Tests.Messages;
using Abc.Zebus.Transport;
using NUnit.Framework;
using ProtoBuf;

namespace Abc.Zebus.Tests.Transport
{
    [TestFixture]
    public class TransportMessageReaderTests
    {
        [Test]
        public void should_read_message()
        {
            var transportMessage = TestDataBuilder.CreateTransportMessage<FakeCommand>();

            var outputStream = new ProtoBufferWriter();
            outputStream.WriteTransportMessage(transportMessage);

            var inputStream = new ProtoBufferReader(outputStream.Buffer, 0, outputStream.Position);
            var deserialized = inputStream.ReadTransportMessage();

            deserialized.Id.ShouldEqual(transportMessage.Id);
            deserialized.MessageTypeId.ShouldEqual(transportMessage.MessageTypeId);
            deserialized.GetContentBytes().ShouldEqual(transportMessage.GetContentBytes());
            deserialized.Originator.ShouldEqualDeeply(transportMessage.Originator);
            deserialized.Environment.ShouldEqual(transportMessage.Environment);
            deserialized.WasPersisted.ShouldEqual(transportMessage.WasPersisted);
        }

        [Test]
        public void should_read_empty_message()
        {
            var transportMessage = new EmptyCommand().ToTransportMessage();

            var outputStream = new ProtoBufferWriter();
            outputStream.WriteTransportMessage(transportMessage);

            var inputStream = new ProtoBufferReader(outputStream.Buffer, 0, outputStream.Position);
            var deserialized = inputStream.ReadTransportMessage();

            deserialized.Id.ShouldEqual(transportMessage.Id);
            deserialized.MessageTypeId.ShouldEqual(transportMessage.MessageTypeId);
            deserialized.Content.ShouldEqual(Stream.Null);
            deserialized.Originator.ShouldEqualDeeply(transportMessage.Originator);
            deserialized.Environment.ShouldEqual(transportMessage.Environment);
            deserialized.WasPersisted.ShouldEqual(transportMessage.WasPersisted);

            var deserializedMessage = deserialized.ToMessage() as EmptyCommand;
            deserializedMessage.ShouldNotBeNull();
        }

        [TestCase("1")]
        [TestCase("123456789")]
        [TestCase("E0A16850-A1DA-4ABB-970A-CFC8F15314CF")]
        [TestCase("\0")]
        [TestCase("\0\0\0\0\0\0")]
        public void should_not_throw_on_invalid_message(string content)
        {
            var buffer = Encoding.ASCII.GetBytes(content);

            var inputStream = new ProtoBufferReader(buffer, 0, buffer.Length);
            TransportMessage transportMessage = null;
            bool? result = null;

            Assert.DoesNotThrow(() => result = inputStream.TryReadTransportMessage(out transportMessage));

            result.ShouldNotBeNull();
            result.ShouldEqual(false);
            transportMessage.ShouldNotBeNull();
            transportMessage.Id.ShouldEqual(default(MessageId));
            transportMessage.Environment.ShouldBeNull();
        }

        [Test]
        public void should_read_message_with_persistent_peer_ids()
        {
            var transportMessage = TestDataBuilder.CreateTransportMessage<FakeCommand>();
            transportMessage.PersistentPeerIds = new List<PeerId>
            {
                new PeerId("Abc.Testing.A"),
                new PeerId("Abc.Testing.B"),
            };

            var outputStream = new ProtoBufferWriter();
            outputStream.WriteTransportMessage(transportMessage);
            outputStream.WritePersistentPeerIds(transportMessage, transportMessage.PersistentPeerIds);

            var inputStream = new ProtoBufferReader(outputStream.Buffer, 0, outputStream.Position);
            var deserialized = inputStream.ReadTransportMessage();

            deserialized.Id.ShouldEqual(transportMessage.Id);
            deserialized.MessageTypeId.ShouldEqual(transportMessage.MessageTypeId);
            deserialized.PersistentPeerIds.ShouldEqual(transportMessage.PersistentPeerIds);
        }

        [Test]
        public void should_read_message_from_protobuf()
        {
            var transportMessage = TestDataBuilder.CreateTransportMessage<FakeCommand>();

            var stream = new MemoryStream();
            Serializer.Serialize(stream, transportMessage);

            var inputStream = new ProtoBufferReader(stream.GetBuffer(), 0, (int)stream.Length);
            var deserialized = inputStream.ReadTransportMessage();

            deserialized.Id.ShouldEqual(transportMessage.Id);
            deserialized.MessageTypeId.ShouldEqual(transportMessage.MessageTypeId);
            deserialized.GetContentBytes().ShouldEqual(transportMessage.GetContentBytes());
            deserialized.Originator.ShouldEqualDeeply(transportMessage.Originator);
            deserialized.Environment.ShouldEqual(transportMessage.Environment);
            deserialized.WasPersisted.ShouldEqual(transportMessage.WasPersisted);
        }

        [Test, Explicit("Manual test")]
        public void MeasureReadPerformance()
        {
            var transportMessage = TestDataBuilder.CreateTransportMessage<FakeCommand>();

            var outputStream = new ProtoBufferWriter();
            outputStream.WriteTransportMessage(transportMessage);

            var inputStream = new ProtoBufferReader(outputStream.Buffer, 0, outputStream.Position);
            inputStream.ReadTransportMessage();

            const int count = 100_000_000;
            using (Measure.Throughput(count))
            {
                for (var i = 0; i < count; i++)
                {
                    inputStream.Position = 0;
                    inputStream.ReadTransportMessage();
                }
            }
        }
    }
}

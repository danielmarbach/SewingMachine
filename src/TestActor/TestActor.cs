﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;
using NUnit.Framework;
using SewingMachine;
using TestActor.Interfaces;

namespace TestActor
{
    [StatePersistence(StatePersistence.Persisted)]
    class TestActor : RawStatePersistentActor, ITestActor
    {
        static long _callCounter;
        static readonly Task<int> Completed = Task.FromResult(0);
        string _key;

        public TestActor(ActorService actorService, ActorId actorId)
            : base(actorService, actorId)
        { }

        protected override Task OnActivateAsync()
        {
            return Completed;
        }

        protected override Task OnPreActorMethodAsync(ActorMethodContext actorMethodContext)
        {
            _key = Interlocked.Increment(ref _callCounter).ToString();
            return base.OnPreActorMethodAsync(actorMethodContext);
        }

        public Task When_accessing_StateManager_should_throw(CancellationToken cancellationToken)
        {
            Assert.ThrowsAsync<InvalidOperationException>(
                () => StateManager.GetStateAsync<int>("count", cancellationToken));

            return Completed;
        }

        public async Task When_Add_should_Get_value(CancellationToken cancellationToken)
        {
            const string expected = "Value";
            var unsafeKey = await Add(_key, expected);

            AssertGet(unsafeKey, expected, _key);
        }

        public async Task When_TryAdd_should_fail_on_existing_key(CancellationToken cancellationToken)
        {
            const string expected = "Value";
            await Add(_key, expected);

            using (var tx = RawStore.BeginTransaction())
            {
                IntPtr
                    unsafeKey;
                Assert.False(RawStore.TryAdd(tx, Create(_key, expected, out unsafeKey)));
            }
        }

        public Task When_TryRemove_non_existent_value_should_not_fail(CancellationToken cancellationToken)
        {
            IntPtr unsafeKey;
            Create(_key, "Value", out unsafeKey);

            using (var tx = RawStore.BeginTransaction())
            {
                unsafe
                {
                    Assert.False(RawStore.TryRemove(tx, (char*)unsafeKey, 0), "Should not remove non-existing entry");
                }
            }

            return Completed;
        }

        public Task When_Remove_non_existent_value_should_throw(CancellationToken cancellationToken)
        {
            IntPtr unsafeKey;
            Create(_key, "Value", out unsafeKey);

            using (var tx = RawStore.BeginTransaction())
            {
                unsafe
                {
                    Assert.Throws(Is.AssignableTo<Exception>(), () => RawStore.Remove(tx, (char*)unsafeKey, 0), "Should not remove non-existing entry");
                }
            }

            return Completed;
        }

        public async Task When_Update_existent_value_should_replace(CancellationToken cancellationToken)
        {
            var unsafeKey = await Add(_key, "Value");

            using (var tx = RawStore.BeginTransaction())
            {
                unsafe
                {
                    var item = (Item)RawStore.TryGet(tx, (char*)unsafeKey, Map);
                    RawStore.Update(tx, Create(_key, "V", out unsafeKey), item.SequenceNumber);
                }

                await tx.CommitAsync();
            }

            AssertGet(unsafeKey, "V", _key);
        }

        public async Task When_TryUpdate_existent_value_with_wrong_version_should_throw(CancellationToken cancellationToken)
        {
            var unsafeKey = await Add(_key, "Value");

            using (var tx = RawStore.BeginTransaction())
            {
                unsafe
                {
                    var item = (Item)RawStore.TryGet(tx, (char*)unsafeKey, Map);
                    Assert.Throws<COMException>(() => RawStore.TryUpdate(tx, Create(_key, "V", out unsafeKey), item.SequenceNumber + 1));
                }

                await tx.CommitAsync();
            }

            AssertGet(unsafeKey, "Value", _key);
        }

        public async Task When_Enumerate_should_go_through_all_prefixed_value(CancellationToken cancellationToken)
        {
            const string expected = "Value";
            var unsafeKey = await Add(_key, expected);
            await Add(_key + "1", expected);
            await Add(_key + "2", expected);

            using (var tx = RawStore.BeginTransaction())
            {
                unsafe
                {
                    var results = new List<object>();
                    RawStore.Enumerate(tx, (char*)unsafeKey, Map, o => results.Add(o));

                    var array = results.Cast<Item>().OrderBy(i => i.Key).ToArray();

                    Assert.AreEqual(3, array.Length);

                    Assert.AreEqual(_key, array[0].Key);
                    Assert.AreEqual(expected, array[0].Value);

                    Assert.AreEqual(_key + "1", array[1].Key);
                    Assert.AreEqual(expected, array[1].Value);

                    Assert.AreEqual(_key + "2", array[2].Key);
                    Assert.AreEqual(expected, array[2].Value);

                }
            }
        }

        async Task<IntPtr> Add(string key, string expected)
        {
            IntPtr unsafeKey;
            using (var tx = RawStore.BeginTransaction())
            {
                RawStore.Add(tx, Create(key, expected, out unsafeKey));
                await tx.CommitAsync().ConfigureAwait(false);
            }
            return unsafeKey;
        }

        unsafe void AssertGet(IntPtr unsafeKey, string expected, string key)
        {
            using (var tx = RawStore.BeginTransaction())
            {
                var item = (Item)RawStore.TryGet(tx, (char*)unsafeKey, Map);

                Assert.NotNull(item);

                Assert.AreEqual(expected, item.Value);
                Assert.AreEqual(key, item.Key);
            }
        }

        static unsafe ReplicaKeyValue Create(string key, string value, out IntPtr unsafeKey)
        {
            unsafeKey = Marshal.StringToHGlobalUni(key);
            var k = (char*)unsafeKey;
            var v = (char*)Marshal.StringToHGlobalUni(value);
            return new ReplicaKeyValue(k, (byte*)v, (value.Length + 1) * 2);
        }

        static unsafe object Map(RawAccessorToKeyValueStoreReplica.RawItem arg)
        {
            var value = new string((char*)arg.Value, 0, arg.ValueLength / 2 - 1);
            var key = new string(arg.Key);

            return new Item
            {
                Key = key,
                Value = value,
                SequenceNumber = arg.SequenceNumber
            };
        }

        class Item
        {
            public string Value { get; set; }
            public string Key { get; set; }
            public long SequenceNumber { get; set; }
        }
    }
}

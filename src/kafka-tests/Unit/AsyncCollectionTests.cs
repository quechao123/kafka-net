﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KafkaNet.Common;
using NUnit.Framework;

namespace kafka_tests.Unit
{
    [TestFixture]
    public class AsyncCollectionTests
    {
        [Test]
        public void AsyncCollectionConstructs()
        {
            var collection = new AsyncCollection<string>();
            Assert.That(collection, Is.Not.Null);
        }

        [Test]
        public void OnDataAvailableShouldTriggerWhenDataAdded()
        {
            var aq = new AsyncCollection<bool>();

            Assert.That(aq.OnDataAvailable(CancellationToken.None).IsCompleted, Is.False, "Task should indicate no data available.");

            aq.Add(true);

            Assert.That(aq.OnDataAvailable(CancellationToken.None).IsCompleted, Is.True, "Task should indicate data available.");
        }

        [Test]
        public void OnDataAvailableShouldBlockWhenDataRemoved()
        {
            var aq = new AsyncCollection<bool>();

            aq.Add(true);

            Assert.That(aq.OnDataAvailable(CancellationToken.None).IsCompleted, Is.True, "Task should indicate data available.");

            bool data;
            aq.TryTake(out data);
            Assert.That(aq.OnDataAvailable(CancellationToken.None).IsCompleted, Is.False, "Task should indicate no data available.");
        }

        [Test]
        [ExpectedException(typeof(OperationCanceledException))]
        public async void OnDataAvailableShouldCancel()
        {
            var aq = new AsyncCollection<bool>();
            var cancelToken = new CancellationTokenSource();
            Task.Delay(TimeSpan.FromMilliseconds(100)).ContinueWith(t => cancelToken.Cancel());

            await aq.OnDataAvailable(cancelToken.Token);
        }

        [Test]
        public void DrainShouldBlockWhenDataRemoved()
        {
            var aq = new AsyncCollection<bool>();

            aq.Add(true);
            aq.Add(true);

            Assert.That(aq.OnDataAvailable(CancellationToken.None).IsCompleted, Is.True, "Task should indicate data available.");

            var drained = aq.Drain().ToList();
            Assert.That(aq.OnDataAvailable(CancellationToken.None).IsCompleted, Is.False, "Task should indicate no data available.");
        }

        [Test]
        public void TryTakeShouldReturnFalseOnEmpty()
        {
            var aq = new AsyncCollection<bool>();

            Assert.That(aq.OnDataAvailable(CancellationToken.None).IsCompleted, Is.False, "Task should indicate no data available.");

            bool data;
            Assert.That(aq.TryTake(out data), Is.False, "TryTake should report false on empty collection.");
            Assert.That(aq.OnDataAvailable(CancellationToken.None).IsCompleted, Is.False, "Task should indicate no data available.");
        }

        [Test]
        public async void CollectionShouldReportCorrectBufferCount()
        {
            var collection = new AsyncCollection<int>();

            var dataTask = collection.TakeAsync(10, TimeSpan.FromHours(5), CancellationToken.None);

            collection.AddRange(Enumerable.Range(0, 9));
            Assert.That(collection.Count, Is.EqualTo(9));

            collection.Add(1);
            var data = await dataTask;
            Assert.That(data.Count, Is.EqualTo(10));
            Assert.That(collection.Count, Is.EqualTo(0));
        }

        #region Take Tests...
        [Test]
        public async void TakeAsyncShouldOnlyWaitTimeoutAndReturnWhatItHas()
        {
            const int size = 20;
            var aq = new AsyncCollection<bool>();

            Task.Factory.StartNew(() =>
            {
                //this should take 2000ms to complete
                for (int i = 0; i < size; i++)
                {
                    aq.Add(true);
                    Thread.Sleep(100);
                }
            });

            var result = await aq.TakeAsync(size, TimeSpan.FromMilliseconds(100), CancellationToken.None);

            Assert.That(result.Count, Is.LessThan(size));
        }

        [Test]
        public async void TakeAsyncShouldReturnEmptyListIfNothingFound()
        {
            var aq = new AsyncCollection<bool>();

            var result = await aq.TakeAsync(100, TimeSpan.FromMilliseconds(100), CancellationToken.None).ConfigureAwait(false);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Count, Is.EqualTo(0));
        }

        [Test]
        public async void TakeAsyncShouldBeAbleToCancel()
        {
            var cancelSource = new CancellationTokenSource();
            var collection = new AsyncCollection<int>();

            Task.Delay(TimeSpan.FromMilliseconds(100)).ContinueWith(t => cancelSource.Cancel());

            var sw = Stopwatch.StartNew();
            var data = await collection.TakeAsync(10, TimeSpan.FromMilliseconds(500), cancelSource.Token);
            sw.Stop();

            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(300));
        }


        [Test]
        public async void TakeAsyncShouldRemoveItemsFromCollection()
        {
            const int expectedCount = 10;

            var collection = new AsyncCollection<int>();
            collection.AddRange(Enumerable.Range(0, expectedCount));

            var data = await collection.TakeAsync(expectedCount, TimeSpan.FromMilliseconds(100), CancellationToken.None);

            Assert.That(data.Count, Is.EqualTo(expectedCount));
            Assert.That(collection.Count, Is.EqualTo(0));
        }


        [Test]
        public async void TakeAsyncShouldWaitXForBatchSizeToCollect()
        {
            const int expectedDelay = 100;
            const int expectedCount = 10;

            var collection = new AsyncCollection<int>();
            collection.AddRange(Enumerable.Range(0, expectedCount));

            var sw = Stopwatch.StartNew();
            var data = await collection.TakeAsync(expectedCount + 1, TimeSpan.FromMilliseconds(expectedDelay), CancellationToken.None);

            Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(expectedDelay));
            Assert.That(data.Count, Is.EqualTo(expectedCount));
        }

        [Test]
        public async void TakeAsyncShouldReturnAsSoonAsBatchSizeArrived()
        {
            var collection = new AsyncCollection<int>();

            var dataTask = collection.TakeAsync(10, TimeSpan.FromSeconds(5), CancellationToken.None);

            collection.AddRange(Enumerable.Range(0, 10));

            await dataTask;

            Assert.That(collection.Count, Is.EqualTo(0));

        }
        #endregion

        #region Thread Contention Tests...
        [Test]
        public async void TakeAsyncShouldReturnEvenWhileMoreDataArrives()
        {
            var exit = false;
            var collection = new AsyncCollection<int>();

            var sw = Stopwatch.StartNew();
            var dataTask = collection.TakeAsync(10, TimeSpan.FromMilliseconds(5000), CancellationToken.None);


            var highVolumeAdding = Task.Factory.StartNew(() =>
            {
                //high volume of data adds
                while (exit == false)
                {
                    collection.Add(1);
                    Thread.Sleep(5);
                }
            });

            Console.WriteLine("Awaiting data...");
            await dataTask;

            Assert.That(dataTask.Result.Count, Is.EqualTo(10));
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(5000));
            exit = true;

            Console.WriteLine("Waiting to unwind test...");
            await highVolumeAdding;
        }


        [Test]
        public void TakeAsyncShouldPlayNiceWithTPL()
        {
            const int expected = 200;
            const int max = 400;
            var exit = false;
            var collection = new AsyncCollection<int>();

            var dataTask = collection.TakeAsync(expected, TimeSpan.FromSeconds(100), CancellationToken.None);

            dataTask.ContinueWith(x => exit = true);

            Parallel.ForEach(Enumerable.Range(0, max).ToList(),
                   new ParallelOptions { MaxDegreeOfParallelism = 20 },
                   x =>
                   {
                       while (exit == false)
                       {
                           collection.Add(x);
                           Thread.Sleep(100);
                       }
                   });

            Console.WriteLine("Left in collection: {0}", collection.Count);
            Assert.That(dataTask.Result.Count, Is.EqualTo(expected));
            Assert.That(collection.Count, Is.LessThan(max - expected));
        }

        [Test]
        public void TakeAsyncShouldBeThreadSafe()
        {
            const int expected = 10;
            const int max = 100;
            var exit = false;
            var collection = new AsyncCollection<int>();

            var take1 = collection.TakeAsync(expected, TimeSpan.FromSeconds(100), CancellationToken.None);
            var take2 = collection.TakeAsync(expected, TimeSpan.FromSeconds(100), CancellationToken.None);
            var take3 = collection.TakeAsync(expected, TimeSpan.FromSeconds(100), CancellationToken.None);

            take1.ContinueWith(t => Console.WriteLine("Take1 done..."));
            take2.ContinueWith(t => Console.WriteLine("Take2 done..."));
            take3.ContinueWith(t => Console.WriteLine("Take3 done..."));
            Task.WhenAll(take1, take2, take3).ContinueWith(x => exit = true);

            Parallel.ForEach(Enumerable.Range(0, max).ToList(),
                   new ParallelOptions { MaxDegreeOfParallelism = 20 },
                   x =>
                   {
                       while (exit == false)
                       {
                           collection.Add(x);
                           Thread.Sleep(100);
                       }
                   });

            Console.WriteLine("Left in collection: {0}", collection.Count);
            Assert.That(take1.Result.Count, Is.EqualTo(expected));
            Assert.That(take2.Result.Count, Is.EqualTo(expected));
            Assert.That(take3.Result.Count, Is.EqualTo(expected));
            Assert.That(collection.Count, Is.LessThan(max - (expected * 3)));
        }
        #endregion


        #region CompletedTests Tests...
        
        [Test]
        [ExpectedException(typeof(ObjectDisposedException))]
        public void CompletedCollectionShouldPreventMoreItemsAdded()
        {
            var collection = new AsyncCollection<int>();
            collection.Add(1);
            collection.CompleteAdding();
            collection.Add(1);
        }

        [Test]
        public void CompletedCollectionShouldShowCompletedTrue()
        {
            var collection = new AsyncCollection<int>();
            collection.Add(1);
            Assert.That(collection.IsCompleted, Is.False);
            collection.CompleteAdding();
            Assert.That(collection.IsCompleted, Is.True);
        }
        #endregion
    }
}

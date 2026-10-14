// The window's job runs on BackgroundScheduler so a long remesh leaves the machine usable. That only
// holds while the solve's Parallel calls stay on its threads: given no options they went to the
// shared pool at normal priority (19,585 of 20,000 iterations, measured), which is the lag ccnb at
// 100,000 quads caused.

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class BackgroundSchedulerTests
    {
        [Test]
        public void ParallelWorkInsideAJobStaysOnItsThreads()
        {
            using var scheduler = new BackgroundScheduler(3);
            var names = new ConcurrentDictionary<string, System.Threading.ThreadPriority>();
            scheduler.Run(() =>
            {
                Parallel.For(0, 2000, BackgroundScheduler.Options, i =>
                {
                    names[Thread.CurrentThread.Name ?? "unnamed"] = Thread.CurrentThread.Priority;
                    Thread.SpinWait(500);
                });
                return 0;
            }, CancellationToken.None).Wait();

            Assert.That(names.Count, Is.GreaterThan(0));
            foreach (var kv in names)
            {
                StringAssert.StartsWith("Native Remesher", kv.Key);
                Assert.That(kv.Value, Is.EqualTo(System.Threading.ThreadPriority.BelowNormal));
            }
        }

        [Test]
        public void PipelineRunsTheSameOnTheScheduler()
        {
            // A UV sphere, 24 x 48.
            var p = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();
            const int lat = 24, lon = 48;
            for (var j = 0; j <= lat; j++)
                for (var i = 0; i < lon; i++)
                {
                    float th = Mathf.PI * j / lat, ph = 2f * Mathf.PI * i / lon;
                    p.Add(new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph)));
                }
            for (var j = 0; j < lat; j++)
                for (var i = 0; i < lon; i++)
                {
                    int a = j * lon + i, b = j * lon + (i + 1) % lon, c = a + lon, d = b + lon;
                    if (j > 0) t.AddRange(new[] { a, b, c });
                    if (j < lat - 1) t.AddRange(new[] { b, d, c });
                }
            // Weld each pole's ring to one vertex.
            for (var k = 0; k < t.Count; k++)
            {
                if (t[k] < lon) t[k] = 0;
                else if (t[k] >= lat * lon) t[k] = lat * lon;
            }
            var positions = p.ToArray();
            var triangles = t.ToArray();
            var settings = RemeshSettings.Default;
            settings.targetQuadCount = 300;
            var profile = RemeshProfile.For(RemeshProfileKind.GameAsset);
            var direct = RemeshPipeline.Solve(positions, triangles, settings, profile);
            using var scheduler = new BackgroundScheduler();
            var scheduled = scheduler.Run(() => RemeshPipeline.Solve(positions, triangles, settings, profile),
                CancellationToken.None).Result;
            Assert.Greater(direct.extraction.faceCount, 0);
            Assert.AreEqual(direct.extraction.faceCount, scheduled.extraction.faceCount);
        }
    }
}

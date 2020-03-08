using System;
using Xunit;
using Speckle.Serialisation;
using Speckle.Models;
using System.Collections.Generic;
using Speckle.Kits;
using Speckle.Core;
using Speckle.Transports;
using System.Diagnostics;
using Xunit.Abstractions;

namespace Tests
{
  public class Serialization
  {
    private readonly ITestOutputHelper output;

    public Serialization(ITestOutputHelper output)
    {
      this.output = output;
    }

    [Fact]
    public void KitsExist()
    {
      var kits = KitManager.Kits;
      Assert.NotEmpty(kits);

      var types = KitManager.Types;
      Assert.NotEmpty(types);
    }

    [Fact]
    public void SimpleSerialization()
    {
      var serializer = new Serializer();

      var table = new DiningTable();
      ((dynamic)table)["@wonkyVariable_Name"] = new TableLegFixture();

      var result = serializer.Serialize(table);

      var test = serializer.Deserialize(result);

      Assert.Equal(test.hash, table.hash);

      var polyline = new Polyline();

      for (int i = 0; i < 100; i++)
        polyline.Points.Add(new Point() { X = i * 2, Y = i % 2 });

      var strPoly = serializer.Serialize(polyline);
      var dePoly = serializer.Deserialize(strPoly);

      Assert.Equal(polyline.hash, dePoly.hash);

    }

    [Fact]
    public void DiskTransportSerialization()
    {
      var transport = new DiskTransport();
      var serializer = new Serializer();

      var table = new DiningTable();

      var result = serializer.SerializeAndSave(table, transport);

      var test = serializer.DeserializeAndGet(result, transport);

      Assert.Equal(test.hash, table.hash);
    }

    [Fact]
    public void MemoryTransportSerialization()
    {
      var transport = new MemoryTransport();
      var serializer = new Serializer();

      var table = new DiningTable();

      var result = serializer.SerializeAndSave(table, transport);

      var test = serializer.DeserializeAndGet(result, transport);

      Assert.Equal(test.hash, table.hash);
    }

    [Fact]
    public void DynamicDispatchment()
    {
      var pt = new Point(1, 2, 3);
      ((dynamic)pt)["@detach_me"] = new Point(3, 4, 5);
      ((dynamic)pt)["@detach_me_too"] = new Point(3, 4, 5); // same point, same hash, should not create a new object in the transport.

      var transport = new MemoryTransport();
      var serializer = new Serializer();

      var result = serializer.SerializeAndSave(pt, transport);

      Assert.Equal(2, transport.Objects.Count);

      var deserialized = serializer.DeserializeAndGet(result, transport);

      Assert.Equal(pt.hash, deserialized.hash);

    }

    [Fact]
    public void AbstractObjectHandling()
    {
      var nk = new NonKitClass() { TestProp = "Hello", Numbers = new List<int>() { 1, 2, 3, 4, 5 } };
      var abs = new Abstract(nk);

      var transport = new MemoryTransport();
      var serializer = new Serializer();

      var abs_serialized = serializer.Serialize(abs);
      var abs_deserialized = serializer.Deserialize(abs_serialized);
      var abs_se_deserializes = serializer.Serialize(abs_deserialized);

      Assert.Equal(abs.hash, abs_deserialized.hash);
      Assert.Equal(abs.@base.GetType(), ((Abstract)abs_deserialized).@base.GetType());
    }

    [Fact]
    public void IgnoreCircularReferences()
    {
      var pt = new Point(1,2,3);
      ((dynamic)pt).circle = pt;

      var test = (new Serializer()).Serialize(pt);
      var tt = test;

      var memTransport = new MemoryTransport();
      var test2 = (new Serializer()).SerializeAndSave(pt, memTransport);
      var ttt = test2;

      var test2_deserialized = (new Serializer()).DeserializeAndGet(ttt, memTransport);
      var t = test2_deserialized;
    }

  }

  public class Hashing
  {

    private readonly ITestOutputHelper output;

    public Hashing(ITestOutputHelper output)
    {
      this.output = output;
    }

    [Fact]
    public void HashChangeCheck()
    {
      var table = new DiningTable();
      var secondTable = new DiningTable();

      Assert.Equal(table.hash, secondTable.hash);

      ((dynamic)secondTable).testProp = "wonderful";

      Assert.NotEqual(table.hash, secondTable.hash);
    }

    [Fact]
    public void IgnoredDynamicPropertiesCheck()
    {
      var table = new DiningTable();
      var originalHash = table.hash;

      ((dynamic)table).__testProp = "wonderful";

      Assert.Equal(originalHash, table.hash);
    }

    [Fact]
    public void IgnoreFlaggedProperties()
    {
      var table = new DiningTable();
      var h1 = table.hash;
      table.HashIngoredProp = "adsfghjkl";

      Assert.Equal(h1, table.hash);
    }

    [Fact]
    public void HashingPerformance()
    {
      var polyline = new Polyline();

      for (int i = 0; i < 10000; i++)
        polyline.Points.Add(new Point() { X = i * 2, Y = i % 2 });

      var stopWatch = new Stopwatch();
      stopWatch.Start();

      // Warm-up: first hashing always takes longer due to json serialisation init
      var h1 = polyline.hash;
      var stopWatchStep = stopWatch.ElapsedMilliseconds;

      stopWatchStep = stopWatch.ElapsedMilliseconds;
      var h2 = polyline.hash;

      var diff1 = stopWatch.ElapsedMilliseconds - stopWatchStep;
      Assert.True( diff1 < 200, $"Hashing shouldn't take that long ({diff1} ms) for the test object used.");
      output.WriteLine($"Big obj hash duration: {diff1} ms");

      var pt = new Point() { X = 10, Y = 12, Z = 30 };
      stopWatchStep = stopWatch.ElapsedMilliseconds;
      var h3 = pt.hash;

      var diff2 = stopWatch.ElapsedMilliseconds - stopWatchStep;
      Assert.True( diff2 < 10, $"Hashing shouldn't take that long  ({diff2} ms)for the point object used.");
      output.WriteLine($"Small obj hash duration: {diff2} ms");
    }

    [Fact]
    public void AbstractHashing()
    {
      var nk1 = new NonKitClass();
      var abs1 = new Abstract(nk1);

      var nk2 = new NonKitClass() { TestProp = "HEllo" };
      var abs2 = new Abstract(nk2);

      var abs1H = abs1.hash;
      var abs2H = abs2.hash;

      Assert.NotEqual(abs1H, abs2H);
      
      nk1.TestProp = "Wow";

      Assert.NotEqual(abs1H, abs1.hash);
    }
  }
}

using System.Text.Json;
using Nexus.Contracts;

namespace Nexus.Tests;

public sealed class CompactionTests
{
    private static ReadResult Sample()
    {
        var group = new PropertyGroup("Sheet · Identity Data");
        group.Properties.Add(new PropertyValue { Name = "Sheet Name", Value = "Floor Plan", RawValue = "Floor Plan", StorageType = "String" });
        group.Properties.Add(new PropertyValue { Name = "Scale", Value = "1/8\" = 1'-0\"", RawValue = "96", StorageType = "Integer" });
        group.Properties.Add(new PropertyValue { Name = "Derived only", Value = "Yes", RawValue = null });
        group.Properties.Add(new PropertyValue { Name = "Empty", Value = null, RawValue = null, HasValue = false });
        group.Properties.Add(new PropertyValue { Name = "Locked", Value = "A", RawValue = "A", IsReadOnly = true, ReadOnlyReason = "Borrowed" });
        var item = new DataItem { Id = "1", Name = "A101 - Floor Plan" };
        item.Groups.Add(group);
        return new ReadResult { Items = { item } };
    }

    private static ReadResult RoundTrip(ReadResult r) =>
        JsonSerializer.Deserialize<ReadResult>(JsonSerializer.Serialize(r, Json.Options), Json.Options)!;

    [Fact]
    public void Compact_then_expand_gives_back_the_same_values()
    {
        var sent = Sample();
        Compaction.Compact(sent);
        var received = RoundTrip(sent);
        Compaction.Expand(received);

        var expected = Sample().Items[0].Groups[0].Properties;
        var actual = received.Items[0].Groups[0].Properties;
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Value, actual[i].Value);
            Assert.Equal(expected[i].RawValue, actual[i].RawValue); // null stays null: writers skip the check then
            Assert.Equal(expected[i].IsReadOnly, actual[i].IsReadOnly);
            Assert.Equal(expected[i].HasValue, actual[i].HasValue);
        }
    }

    [Fact]
    public void Compact_result_is_smaller()
    {
        var full = Sample();
        var compact = Sample();
        Compaction.Compact(compact);
        Assert.True(JsonSerializer.Serialize(compact, Json.Options).Length < JsonSerializer.Serialize(full, Json.Options).Length);
    }

    [Fact]
    public void Results_from_older_agents_are_left_alone()
    {
        var old = RoundTrip(Sample()); // not compacted
        Compaction.Expand(old);
        Assert.Null(old.Items[0].Groups[0].Properties[2].RawValue);
    }
}

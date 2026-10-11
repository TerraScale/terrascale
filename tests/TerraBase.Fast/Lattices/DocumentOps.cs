using System.Text.Json;
using Bogus;
using TerraBase.Engine.Lattices;

namespace TerraBase.Fast.Lattices;

// Random document operations for the DocumentState property tests: few names, values and elements, so replicas
// collide on fields, elements and stamps. Names and elements include non-ASCII ones that sort differently in UTF-16.
internal static class DocumentOps
{
    private static readonly string[] Names = ["a", "b", "é", "", "😀"];
    private static readonly string[] Values = ["1", "-3", "\"x\"", "null", "[1,\"y\"]", "[]", "{\"k\":[2]}", "2.5", "true"];
    private static readonly string[] Elements = ["1", "2", "\"y\"", "\"é\"", "\"😀\""];

    // One request on the replica `node`: one or two operations at one fresh version, as the write lane mints it.
    public static (DocumentState State, Hlc Stamp) Apply(Randomizer random, DocumentState state, ReplicaId node)
    {
        var at = new EntryVersion(new Hlc(Math.Max(random.ULong(1, 64), state.MaxStamp.Value + 1)), node);
        state = One(random, state, at);
        return (random.Int(0, 4) == 0 ? One(random, state, at) : state, at.Stamp);
    }

    public static JsonElement Parse(string json) => JsonElement.Parse(json);

    private static DocumentState One(Randomizer random, DocumentState state, EntryVersion at)
    {
        var name = random.ArrayElement(Names);
        try
        {
            return random.Int(0, 9) switch
            {
                0 => state.Put(at, Parse($"{{\"{random.ArrayElement(Names)}\":{random.ArrayElement(Values)},\"{name}\":{random.ArrayElement(Values)}}}")),
                1 => state.Delete(at),
                2 => state.Unset(at, name),
                3 or 4 => state.Increment(at, name, random.Bool() ? random.Long(1, 5) : -random.Long(1, 5)),
                5 or 6 => state.Add(at, name, Parse(random.ArrayElement(Elements))),
                7 => state.Remove(at, name, Parse(random.ArrayElement(Elements))),
                _ => state.Set(at, name, Parse(random.ArrayElement(Values))),
            };
        }
        catch (InvalidOperationException)
        {
            // The field holds another kind: the client would get 400; write a plain value instead.
            return state.Set(at, name, Parse(random.ArrayElement(Values)));
        }
    }
}

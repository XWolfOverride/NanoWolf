using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;

namespace XWolf.NanoWolf;

/// <summary>Mapping rules for the types of an application (the ones of NanoWolf are registered by NanoWolf itself).</summary>
public static class MongoMapping
{
    private static readonly HashSet<string> Registered = [];

    /// <summary>
    /// Field names in camelCase, enums as text, unknown fields ignored, for the types of the given namespace and its
    /// children. Dates are already stored as BSON dates. Safe to call more than once.
    /// </summary>
    public static void UseConventionsFor(string @namespace)
    {
        lock (Registered)
        {
            if (!Registered.Add(@namespace))
                return;
            ConventionRegistry.Register("App:" + @namespace, new ConventionPack
            {
                new CamelCaseElementNameConvention(),
                new EnumRepresentationConvention(BsonType.String),
                new IgnoreExtraElementsConvention(true),
            }, type => type.Namespace != null && (type.Namespace == @namespace || type.Namespace.StartsWith(@namespace + ".", StringComparison.Ordinal)));
        }
    }
}

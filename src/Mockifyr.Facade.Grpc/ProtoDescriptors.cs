using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Mockifyr.Facade.Grpc;

/// <summary>
/// The gRPC method a request path resolves to: the input/output message descriptors the codec uses
/// to decode the request and encode the response.
/// </summary>
public sealed record GrpcMethod(string Service, string Method, MessageDescriptor InputType, MessageDescriptor OutputType);

/// <summary>
/// Loads compiled proto descriptor sets (<c>*.dsc</c>, produced by <c>protoc --descriptor_set_out
/// --include_imports</c>) and indexes their services/methods by the gRPC path
/// <c>/{package.Service}/{Method}</c> — descriptor sets are loaded from the configured <c>grpc</c>
/// directory (G13, verified by the differential suite). The engine never sees protobuf; this hands the
/// codec the message types.
/// </summary>
/// <remarks>
/// The index is <b>swappable</b> (G18-pre): descriptor upload/delete through the admin API rebuilds it
/// via <see cref="Reload"/> without a restart. Readers take a volatile snapshot, so a reload never
/// tears an in-flight request.
/// </remarks>
public sealed class ProtoDescriptors
{
    private volatile Dictionary<(string Service, string Method), GrpcMethod> _methods;

    private static readonly IEqualityComparer<(string, string)> MethodComparer =
        EqualityComparer<(string, string)>.Default;

    /// <summary>Builds an index from one or more descriptor-set byte blobs (each a <c>FileDescriptorSet</c>).</summary>
    public ProtoDescriptors(IEnumerable<byte[]> descriptorSets) => _methods = Build(descriptorSets);

    /// <summary>Atomically replaces the index with one built from the given descriptor sets.</summary>
    public void Reload(IEnumerable<byte[]> descriptorSets) => _methods = Build(descriptorSets);

    private static Dictionary<(string Service, string Method), GrpcMethod> Build(IEnumerable<byte[]> descriptorSets)
    {
        var methods = new Dictionary<(string Service, string Method), GrpcMethod>(MethodComparer);
        foreach (var bytes in descriptorSets)
        {
            var set = FileDescriptorSet.Parser.ParseFrom(bytes);
            var files = FileDescriptor.BuildFromByteStrings(set.File.Select(file => file.ToByteString()));
            foreach (var file in files)
            {
                foreach (var service in file.Services)
                {
                    foreach (var method in service.Methods)
                    {
                        methods[(service.FullName, method.Name)] =
                            new GrpcMethod(service.FullName, method.Name, method.InputType, method.OutputType);
                    }
                }
            }
        }

        return methods;
    }

    /// <summary>Whether any method is registered (i.e. gRPC serving is available).</summary>
    public bool HasMethods => _methods.Count > 0;

    /// <summary>Every indexed method, ordered by service then method (the admin/UI listing shape).</summary>
    public IReadOnlyList<GrpcMethod> Methods =>
        [.. _methods.Values.OrderBy(m => m.Service, StringComparer.Ordinal).ThenBy(m => m.Method, StringComparer.Ordinal)];

    /// <summary>
    /// Resolves a gRPC request path (<c>/package.Service/Method</c>) to its method, or null when unknown.
    /// </summary>
    public GrpcMethod? Resolve(string path)
    {
        var trimmed = path.AsSpan().TrimStart('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        var service = trimmed[..slash].ToString();
        var method = trimmed[(slash + 1)..].ToString();
        return _methods.TryGetValue((service, method), out var resolved) ? resolved : null;
    }
}

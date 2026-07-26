using Mockifyr.Facade.Admin;
using Mockifyr.Facade.Grpc;

namespace Mockifyr.Server;

/// <summary>
/// Adapts the gRPC descriptor index to the admin facade's protocol probe (G18-pre, ADR 0010): a stub
/// whose URL path resolves to a loaded service/method classifies as <c>grpc</c>. Lives in the
/// composition root because the admin and gRPC facades never reference each other.
/// </summary>
internal sealed class DescriptorProtocolProbe(ProtoDescriptors descriptors) : IStubProtocolProbe
{
    public bool IsGrpcPath(string path) => descriptors.Resolve(path) is not null;
}

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

/// <summary>Assembly images built by hand whose exported types forward types to other assemblies, as the implementation of a reference
/// assembly does: the module holds nothing but the forwarders, which is all a reader of the exported types needs.</summary>
internal static class ForwardingAssemblies
{
    // The tdForwarder flag of ECMA-335 II.23.1.15, which a type forwarder's row of the exported types carries.
    private const TypeAttributes FORWARDER = (TypeAttributes)0x00200000;

    /// <summary>An assembly image that forwards types.</summary>
    /// <param name="assemblyName">The assembly name; its version is 1.0.0.0.</param>
    /// <param name="forwarded">The types it forwards, each with the assembly that holds it.</param>
    public static byte[] Image(string assemblyName, params (string Namespace, string Name, string Target)[] forwarded)
    {
        var mvid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(assemblyName + "|" + string.Join('|', forwarded)))[..16]);
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(assemblyName + ".dll"), metadata.GetOrAddGuid(mvid), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(assemblyName), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1),
                                   MetadataTokens.MethodDefinitionHandle(1));
        foreach (var group in forwarded.GroupBy(type => type.Target, StringComparer.Ordinal))
        {
            var target = metadata.AddAssemblyReference(metadata.GetOrAddString(group.Key), new Version(1, 0, 0, 0), default, default, 0, default);
            foreach (var type in group)
            {
                metadata.AddExportedType(FORWARDER, type.Namespace.Length == 0 ? default : metadata.GetOrAddString(type.Namespace),
                                         metadata.GetOrAddString(type.Name), target, 0);
            }
        }

        var peBuilder = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder(),
                                             deterministicIdProvider: _ => new BlobContentId(mvid, 0x10000000));
        var image = new BlobBuilder();
        peBuilder.Serialize(image);
        return image.ToArray();
    }
}

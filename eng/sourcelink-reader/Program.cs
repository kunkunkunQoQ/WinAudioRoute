using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Reads the SourceLink metadata out of a portable PDB and prints one "key=value" line per fact,
// so eng/verify-sourcelink.ps1 can assert on it without parsing prose.
//
// Usage: sourcelink-reader <path-to-pdb>

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: sourcelink-reader <path-to-pdb>");
    return 2;
}

string pdbPath = args[0];
if (!File.Exists(pdbPath))
{
    Console.Error.WriteLine("pdb-not-found");
    return 2;
}

// The SourceLink custom debug information kind GUID.
var sourceLinkKind = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");

string? sourceLinkJson = null;

using (var stream = File.OpenRead(pdbPath))
using (var provider = MetadataReaderProvider.FromPortablePdbStream(stream))
{
    MetadataReader reader = provider.GetMetadataReader();
    Console.WriteLine($"documents={reader.Documents.Count}");

    // SourceLink CDI is attached to the assembly handle (1), not the module handle (0).
    foreach (CustomDebugInformationHandle handle in reader.GetCustomDebugInformation(MetadataTokens.EntityHandle(1)))
    {
        CustomDebugInformation cdi = reader.GetCustomDebugInformation(handle);
        if (reader.GetGuid(cdi.Kind) == sourceLinkKind)
        {
            sourceLinkJson = Encoding.UTF8.GetString(reader.GetBlobBytes(cdi.Value));
        }
    }
}

Console.WriteLine($"sourcelink.present={(sourceLinkJson is not null ? "yes" : "no")}");

if (sourceLinkJson is not null)
{
    using var document = System.Text.Json.JsonDocument.Parse(sourceLinkJson);

    if (document.RootElement.TryGetProperty("documents", out var documents))
    {
        foreach (JsonProperty entry in documents.EnumerateObject())
        {
            // Source root -> URL template. The template carries the commit SHA.
            Console.WriteLine($"pattern={entry.Name}");
            Console.WriteLine($"template={entry.Value.GetString()}");
        }
    }
}

// Assembly-level metadata: RepositoryUrl and the commit-bearing informational version.
string dllPath = Path.ChangeExtension(pdbPath, ".dll");
if (File.Exists(dllPath))
{
    var paths = new List<string>(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));

    string? directory = Path.GetDirectoryName(dllPath);
    if (directory is not null)
    {
        paths.AddRange(Directory.GetFiles(directory, "*.dll"));
    }

    using var context = new MetadataLoadContext(new PathAssemblyResolver(paths));

    try
    {
        Assembly assembly = context.LoadFromAssemblyPath(dllPath);

        foreach (CustomAttributeData attribute in assembly.GetCustomAttributesData())
        {
            if (attribute.AttributeType.Name == "AssemblyInformationalVersionAttribute"
                && attribute.ConstructorArguments.Count >= 1)
            {
                Console.WriteLine($"informationalVersion={attribute.ConstructorArguments[0].Value}");
            }
            else if (attribute.AttributeType.Name == "AssemblyMetadataAttribute"
                     && attribute.ConstructorArguments.Count >= 2
                     && (attribute.ConstructorArguments[0].Value as string) == "RepositoryUrl")
            {
                Console.WriteLine($"repositoryUrl={attribute.ConstructorArguments[1].Value}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"assemblyMetadata.error={ex.GetType().Name}");
    }
}

return 0;

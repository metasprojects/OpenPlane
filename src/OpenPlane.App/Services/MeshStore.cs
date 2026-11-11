using OpenPlane.Core.Builder;
using OpenPlane.Core.Design;

namespace OpenPlane.App.Services;

/// <summary>
/// Keeps the fixed meshes of planes open in the builder, so the page sends a short id with every analysis instead of
/// megabytes of vertices. Ids are content hashes; the newest 24 sets are kept.
/// </summary>
public sealed class MeshStore
{
    private readonly object _lock = new();
    private readonly LinkedList<(string id, List<MeshData> meshes)> _sets = new();
    private const int Capacity = 24;

    public string Register(List<MeshData> meshes)
    {
        string id = BuilderJson.Sha1(OplanePackage.ToJson(meshes)).Substring(0, 20);
        lock (_lock)
        {
            var node = _sets.First;
            while (node != null)
            {
                if (node.Value.id == id) { _sets.Remove(node); break; }
                node = node.Next;
            }
            _sets.AddFirst((id, meshes));
            while (_sets.Count > Capacity) _sets.RemoveLast();
        }
        return id;
    }

    /// <summary>The meshes for an id; empty for null or "" (a plane without fixed meshes); null when unknown.</summary>
    public List<MeshData>? Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return new List<MeshData>();
        lock (_lock)
            foreach (var s in _sets)
                if (s.id == id) return s.meshes;
        return null;
    }
}

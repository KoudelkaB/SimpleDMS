namespace SimpleDms.Core;

// Each linking pass gets its own scope. Unknown IDs are discovered by walking
// down from the selected root, never by fetching arbitrary IDs from the workbook.
public sealed class ArchiveDriveClient(IDriveClient client, string root) : IDriveClient
{
    readonly Dictionary<string, string[]> paths = new() { [root] = [root] };
    static InvalidOperationException Outside() => new("Položka není uvnitř vybraného archivu nebo byla přesunuta. Operace byla zastavena.");
    async Task<DriveItem> Validate(string[] path, CancellationToken ct)
    {
        DriveItem? item = null;
        for (var i = 0; i < path.Length; i++)
        {
            item = await client.GetAsync(path[i], ct);
            if (item.Id != path[i] || (i > 0 && !item.Parents.Contains(path[i - 1])) ||
                (i < path.Length - 1 && !item.IsFolder) || (i == 0 && !item.IsFolder)) throw Outside();
        }
        return item!;
    }
    async Task<string[]> FindPath(string id, CancellationToken ct)
    {
        if (paths.TryGetValue(id, out var known)) return known;
        var queue = new Queue<string[]>(); queue.Enqueue([root]);
        var visited = new HashSet<string>();
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var path = queue.Dequeue(); var folder = path[^1];
            if (!visited.Add(folder)) continue;
            if (!(await Validate(path, ct)).IsFolder) throw Outside();
            foreach (var child in await client.ListAsync(folder, ct))
            {
                if (!child.Parents.Contains(folder) || path.Contains(child.Id)) continue;
                var childPath = path.Append(child.Id).ToArray(); paths[child.Id] = childPath;
                if (child.Id == id) return childPath;
                if (child.IsFolder) queue.Enqueue(childPath);
            }
        }
        throw Outside();
    }
    public async Task<DriveItem> GetAsync(string id, CancellationToken ct = default)
        => await Validate(await FindPath(id, ct), ct);
    public async Task<IReadOnlyList<DriveItem>> ListAsync(string parent, CancellationToken ct = default)
    {
        var path = await FindPath(parent, ct);
        if (!(await Validate(path, ct)).IsFolder) throw Outside();
        var items = (await client.ListAsync(parent, ct)).Where(x => x.Parents.Contains(parent) && !path.Contains(x.Id)).ToList();
        foreach (var item in items) paths[item.Id] = path.Append(item.Id).ToArray();
        return items;
    }
}

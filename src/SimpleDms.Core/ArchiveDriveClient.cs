namespace SimpleDms.Core;

// Each archive operation gets its own scope. Unknown IDs are discovered by walking
// down from the selected root, never by fetching arbitrary IDs from the workbook.
public sealed class ArchiveDriveClient(IDriveClient client, string root, bool canWrite) : IDriveClient
{
    readonly Dictionary<string, string[]> paths = new() { [root] = [root] };
    static InvalidOperationException Outside() => new("Položka není uvnitř vybraného archivu nebo byla přesunuta. Operace byla zastavena.");
    void RequireWrite()
    {
        if (!canWrite) throw new InvalidOperationException("Archiv je otevřen jen pro čtení.");
    }
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
    public async Task<byte[]> DownloadAsync(string id, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct);
        if (item.IsFolder || item.MimeType == "application/vnd.google-apps.shortcut") throw Outside();
        return await client.DownloadAsync(id, ct);
    }
    async Task<DriveItem> Created(string parent, DriveItem item, CancellationToken ct)
    {
        if (!item.Parents.Contains(parent)) throw Outside();
        paths[item.Id] = (await FindPath(parent, ct)).Append(item.Id).ToArray();
        return item;
    }
    public async Task<DriveItem> CreateFolderAsync(string parent, string name, string operation, CancellationToken ct = default)
    {
        RequireWrite(); if (!(await GetAsync(parent, ct)).IsFolder) throw Outside();
        return await Created(parent, await client.CreateFolderAsync(parent, name, operation, ct), ct);
    }
    public async Task<DriveItem> UploadAsync(string parent, string name, byte[] bytes, string mime, string operation, CancellationToken ct = default)
    {
        RequireWrite(); if (!(await GetAsync(parent, ct)).IsFolder) throw Outside();
        return await Created(parent, await client.UploadAsync(parent, name, bytes, mime, operation, ct), ct);
    }
    public async Task<DriveItem> ReplaceAsync(string id, byte[] bytes, CancellationToken ct = default)
    {
        RequireWrite(); var item = await GetAsync(id, ct);
        if (item.IsFolder || item.MimeType == "application/vnd.google-apps.shortcut") throw Outside();
        return await client.ReplaceAsync(id, bytes, ct);
    }
    public async Task<DriveItem> MoveAsync(string id, string parent, CancellationToken ct = default)
    {
        RequireWrite(); if (id == root) throw Outside();
        await GetAsync(id, ct);
        if (!(await GetAsync(parent, ct)).IsFolder || (await FindPath(parent, ct)).Contains(id)) throw Outside();
        var item = await client.MoveAsync(id, parent, ct);
        paths.Clear(); paths[root] = [root];
        return await Created(parent, item, ct);
    }
}

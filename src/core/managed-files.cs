namespace OptiScalerBackup.Core;

public sealed record FileRequest(string Path, bool External = false);

public static class ManagedFiles
{
    // Prepare a complete proposal without saving; callers approve once before Put.
    public static Game Prepare(Game game, IEnumerable<FileRequest> requests, string home, string payloadRoot)
    {
        var next = Format.Clone(game);
        foreach (var request in requests)
        {
            Target target;
            if (request.External)
            {
                if (!System.IO.Path.IsPathFullyQualified(request.Path)) throw new ArgumentException("External files require an absolute path.");
                var file = System.IO.Path.GetFullPath(request.Path);
                SafePaths.NoLinks(file);
                if (!SafePaths.FileExists(file)) throw new IOException($"External settings file does not exist: {file}");
                if (SafePaths.Within(game.Directory, file)) target = new("game", SafePaths.Relative(System.IO.Path.GetRelativePath(game.Directory, file)));
                else
                {
                    var root = System.IO.Path.GetDirectoryName(file)!;
                    var id = next.ExternalRoots.FirstOrDefault(p => p.Value.Equals(root, StringComparison.OrdinalIgnoreCase)).Key ?? Format.Id();
                    next.ExternalRoots[id] = root;
                    target = new(id, SafePaths.Relative(System.IO.Path.GetFileName(file)));
                }
            }
            else target = new("game", SafePaths.Relative(request.Path));
            var destination = SafePaths.Resolve(next, target);
            if (Directory.Exists(destination) || InStorage(home, destination) || InStorage(payloadRoot, destination))
                throw new IOException($"Invalid managed destination: {destination}");
            if (!next.Managed.Any(t => PhysicalPath(next, t).Equals(destination, StringComparison.OrdinalIgnoreCase))) next.Managed.Add(target);
        }
        var destinations = next.Managed.Select(t => PhysicalPath(next, t));
        SafePaths.DistinctDestinations(destinations);
        return next;
    }

    private static bool InStorage(string root, string path) => root.Equals(path, StringComparison.OrdinalIgnoreCase) || SafePaths.Within(root, path);
    private static string PhysicalPath(Game game, Target target) => System.IO.Path.GetFullPath(System.IO.Path.Combine(
        target.Root == "game" ? game.Directory : game.ExternalRoots[target.Root], SafePaths.Relative(target.Path)));
}

namespace SerialLog.Core.Configuration;

internal static class WorkspaceCommandGroups
{
    public static void Validate(WorkspaceConfig config)
    {
        if (config.CommandGroups is null || config.CommandGroupQueue is null ||
            config.CommandGroups.Any(g => g is null || !Guid.TryParseExact(g.Id, "N", out _) || g.TargetIds is null || g.Commands is null) ||
            config.CommandGroups.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != config.CommandGroups.Count)
            throw new InvalidDataException("命令组身份或配置无效，原文件已保留。");
        var ids = config.CommandGroups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        if (config.CommandGroupLists is null ||
            config.CommandGroupLists.Any(list => list is null || !Guid.TryParseExact(list.Id, "N", out _) || string.IsNullOrWhiteSpace(list.Name)) ||
            config.CommandGroupLists.Select(list => list.Id).Distinct(StringComparer.Ordinal).Count() != config.CommandGroupLists.Count ||
            config.CommandGroupLists.Select(list => list.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.CommandGroupLists.Count)
            throw new InvalidDataException("命令组列表名称或身份无效，名称不能为空或重复。原文件已保留。");
        var listIds = config.CommandGroupLists.Select(list => list.Id).ToHashSet(StringComparer.Ordinal);
        if (config.SelectedCommandGroupListId != string.Empty && !listIds.Contains(config.SelectedCommandGroupListId) ||
            config.CommandGroups.Any(group => group.ListIds is null || group.ListIds.Any(id => !listIds.Contains(id)) || group.ListIds.Distinct(StringComparer.Ordinal).Count() != group.ListIds.Count))
            throw new InvalidDataException("命令组所属列表或当前列表引用无效，原文件已保留。");
        if (config.CommandGroupQueue.Any(q => q is null || !Guid.TryParseExact(q.Id, "N", out _) || !ids.Contains(q.GroupId)) ||
            config.CommandGroupQueue.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count() != config.CommandGroupQueue.Count)
            throw new InvalidDataException("命令组队列身份或引用无效，原文件已保留。");
    }

    // 仅移除旧预览自动生成且未重命名的分类；用户创建或改名的分类保留。
    public static bool RemoveLegacyDefaultList(WorkspaceConfig config)
    {
        if (config.CommandGroupLists is null) return false;
        var matches = config.CommandGroupLists.Where(list => list is not null && list.Id == CommandGroupListConfig.DefaultId).ToArray();
        if (matches.Length != 1 || matches[0].Name?.Trim() != "默认列表") return false;
        config.CommandGroupLists.Remove(matches[0]);
        foreach (var group in config.CommandGroups ?? []) group?.ListIds?.RemoveAll(id => id == CommandGroupListConfig.DefaultId);
        if (config.SelectedCommandGroupListId == CommandGroupListConfig.DefaultId) config.SelectedCommandGroupListId = string.Empty;
        return true;
    }
}

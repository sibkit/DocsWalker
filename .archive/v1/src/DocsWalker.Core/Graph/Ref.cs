namespace DocsWalker.Core.Graph;

/// <summary>
/// Исходящая связь узла: имя связи + id цели. Связь path хранится с Name="path";
/// прочие связи — с именем, объявленным в типе узла-источника
/// (см. docs/DocsWalker.yml/«связь (ref)»).
/// </summary>
public readonly record struct OutRef(string Name, int TargetId);

/// <summary>
/// Входящая связь на узел: имя связи + id источника. Получается обратным
/// проходом по графу через <see cref="Graph.GetInRefs"/>; в YAML не хранится
/// (см. docs/DocsWalker.yml/«in_refs»).
/// </summary>
public readonly record struct InRef(string Name, int SourceId);

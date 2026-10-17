using System.Collections.Concurrent;

namespace Nodus.Api.Common.Resolvers;

public delegate TKey DictionarySetter<in TInterface, out TKey>(TInterface implementation);
public delegate bool KeySelector<in TInterface, in TKey, in TInput>(TKey key, TInput input);
public delegate bool FallbackKeySelector<in TInterface, in TKey, in TInput>(TKey key, TInput input);

/// <summary>Maps each key to the implementation types registered for it; built once from the container.</summary>
public sealed class SelectionDictionary<TInterface, TKey>(DictionarySetter<TInterface, TKey> dictionarySetter)
    : ISelectionDictionary<TInterface, TKey>
    where TInterface : class
    where TKey : notnull
{
    private ConcurrentDictionary<TKey, List<Type>>? _dictionary;

    public IReadOnlyDictionary<TKey, List<Type>> Get(IServiceProvider serviceProvider) =>
        _dictionary ??= Build(serviceProvider);

    private ConcurrentDictionary<TKey, List<Type>> Build(IServiceProvider serviceProvider)
    {
        var dictionary = new ConcurrentDictionary<TKey, List<Type>>();
        foreach (var service in serviceProvider.GetServices<TInterface>())
        {
            var type = service.GetType();
            dictionary.AddOrUpdate(dictionarySetter(service), _ => [type], (_, list) =>
            {
                list.Add(type);
                return list;
            });
        }

        return dictionary;
    }
}

public sealed class SelectionResolver<TInterface, TKey, TInput>(
    KeySelector<TInterface, TKey, TInput> keySelector,
    FallbackKeySelector<TInterface, TKey, TInput> fallbackKeySelector,
    ISelectionDictionary<TInterface, TKey> selectionDictionary,
    IServiceProvider serviceProvider)
    : ISelectionResolver<TInput, TInterface>
    where TInterface : class
    where TKey : notnull
{
    public TInterface? GetOrDefault(TInput input)
    {
        var map = selectionDictionary.Get(serviceProvider);
        var type = map.FirstOrDefault(pair => keySelector(pair.Key, input)).Value?.First()
                   ?? map.FirstOrDefault(pair => fallbackKeySelector(pair.Key, input)).Value?.First();
        return type is null ? null : (TInterface)serviceProvider.GetRequiredService(type);
    }
}

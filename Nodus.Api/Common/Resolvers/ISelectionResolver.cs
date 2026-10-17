namespace Nodus.Api.Common.Resolvers;

/// <summary>
/// Strategy selection (ported from eatee-backend): picks the registered implementation of
/// <typeparamref name="TImplementation"/> whose key matches the input; null when none does.
/// </summary>
public interface ISelectionResolver<in TInput, out TImplementation>
    where TImplementation : class
{
    TImplementation? GetOrDefault(TInput input);
}

public interface ISelectionDictionary<TInterface, TKey>
    where TInterface : class
    where TKey : notnull
{
    IReadOnlyDictionary<TKey, List<Type>> Get(IServiceProvider serviceProvider);
}

/// <summary>Excludes an implementation from assembly scanning.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RegisterIgnoreAttribute : Attribute;

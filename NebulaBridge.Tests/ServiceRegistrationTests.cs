using Microsoft.Extensions.DependencyInjection;
using NebulaBridge.NativeSources;
using NebulaBridge.Services;

namespace NebulaBridge.Tests;

/// <summary>
/// Jellyfin activates plugin services through Microsoft DI, which throws
/// "constructors are ambiguous" when a type exposes two public constructors of
/// the same arity that are both satisfiable. Unit tests construct these types
/// directly and never notice, so this guards the hosted startup path.
/// </summary>
public sealed class ServiceRegistrationTests
{
    [Fact]
    public void RegisteredImplementationsHaveNoAmbiguousPublicConstructors()
    {
        var services = new ServiceCollection();
        new ServiceRegistrator().RegisterServices(services, null!);

        var offenders = services
            .Select(descriptor => descriptor.ImplementationType)
            .OfType<Type>()
            .Distinct()
            .Where(type => !type.IsAbstract && type.Assembly == typeof(ServiceRegistrator).Assembly)
            .Where(type => type
                .GetConstructors()
                .GroupBy(ctor => ctor.GetParameters().Length)
                .Any(group => group.Count() > 1))
            .Select(type => type.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(typeof(AcquisitionCoordinator))]
    [InlineData(typeof(NativeSourcePipeline))]
    [InlineData(typeof(NativeStreamProxyRegistry))]
    public void OrchestratorConsumersExposeOneConstructorPerArity(Type type)
    {
        var arities = type.GetConstructors().Select(ctor => ctor.GetParameters().Length).ToList();
        Assert.Equal(arities.Distinct().Count(), arities.Count);
        Assert.Contains(type.GetConstructors(), ctor =>
            ctor.GetParameters().Any(p => p.ParameterType == typeof(DebridProviderOrchestrator)));
    }
}

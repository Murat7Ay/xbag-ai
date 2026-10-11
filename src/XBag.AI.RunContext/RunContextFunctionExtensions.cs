using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using XBag.AI.RunContext.Internal;

namespace XBag.AI.RunContext;

/// <summary>Tool helpers: a hidden context parameter and a DI scope per tool call.</summary>
public static class RunContextFunctionExtensions
{
    /// <summary>
    /// Binds every parameter of type <typeparamref name="T"/> to <see cref="RunContext{T}.Current"/> and removes it from the JSON schema
    /// the model sees, so the model can neither see nor fill it.
    /// </summary>
    /// <typeparam name="T">The context type.</typeparam>
    /// <param name="options">The options passed to <c>AIFunctionFactory.Create</c>. An existing <see cref="AIFunctionFactoryOptions.ConfigureParameterBinding"/> keeps handling the other parameters.</param>
    /// <returns>The same options, for chaining (call it once per context type).</returns>
    /// <remarks>
    /// When there is no context, a parameter with a default value (<c>AppUser? user = null</c>) gets <see langword="null"/>; any other
    /// parameter makes the call fail with <see cref="InvalidOperationException"/> before the tool's code runs. An argument the model
    /// sends under the parameter's name is ignored.
    /// </remarks>
    public static AIFunctionFactoryOptions BindRunContext<T>(this AIFunctionFactoryOptions options)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(options);

        Func<ParameterInfo, AIFunctionFactoryOptions.ParameterBindingOptions>? previous = options.ConfigureParameterBinding;
        options.ConfigureParameterBinding = parameter => parameter.ParameterType == typeof(T)
            ? new AIFunctionFactoryOptions.ParameterBindingOptions { BindParameter = Bind, ExcludeFromSchema = true }
            : previous?.Invoke(parameter) ?? default;

        return options;

        static object? Bind(ParameterInfo parameter, AIFunctionArguments arguments) =>
            RunContext<T>.Current ?? (parameter.HasDefaultValue ? null : throw RunContextErrors.Missing(typeof(T)));
    }

    /// <summary>
    /// Wraps <paramref name="function"/> so that every call gets its own <see cref="IServiceScope"/>, disposed when the call ends.
    /// The function sees the scope as <see cref="AIFunctionArguments.Services"/> (an <see cref="IServiceProvider"/> parameter).
    /// </summary>
    /// <param name="function">The tool.</param>
    /// <param name="services">
    /// The provider to create scopes from. When <see langword="null"/>, the one the call arrives with is used
    /// (<c>FunctionInvokingChatClient</c>'s function invocation services, for example from <c>AsAIAgent(..., services: sp)</c>).
    /// </param>
    /// <returns>The wrapped tool.</returns>
    /// <remarks>
    /// <c>FunctionInvokingChatClient</c> passes the provider it was built with, usually the root provider, where a scoped service
    /// (a <c>DbContext</c>) is either refused or shared by every request. This gives each call a fresh scope.
    /// </remarks>
    public static AIFunction WithServiceScope(this AIFunction function, IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new ServiceScopeFunction(function, services);
    }

    private sealed class ServiceScopeFunction(AIFunction innerFunction, IServiceProvider? services) : DelegatingAIFunction(innerFunction)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // ChatClientBuilder.Build() without services hands tools an empty provider, not null: look for the scope factory itself.
            IServiceScopeFactory scopes = (services ?? arguments.Services)?.GetService<IServiceScopeFactory>() ?? throw new InvalidOperationException(
                $"Tool '{Name}' has no service provider to create a scope from. Pass one to WithServiceScope(...), or give the " +
                "function invoker one (Build(services) / UseFunctionInvocation / AsAIAgent with services).");

            AsyncServiceScope scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                AIFunctionArguments scoped = new(arguments)
                {
                    Services = scope.ServiceProvider,
                    Context = arguments.Context,
                };

                // AIFunctionFactory functions serialize their result before returning, so nothing reads the scope after it is disposed.
                return await base.InvokeCoreAsync(scoped, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

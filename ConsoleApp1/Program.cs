using System;
using System.Linq;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Dispatcher;
using System.Threading.Tasks;
using Autofac;
using AutofacSerilogIntegration;
using ConsoleApp1.ServiceReference1;
using Serilog;

namespace ConsoleApp1
{
    class Program
    {
        static async Task Main()
        {
            Log.Logger = new LoggerConfiguration()
                .Enrich.WithThreadId()
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3} {SourceContext}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            var builder = new ContainerBuilder();
            builder.RegisterGeneric(typeof(RemoteCaller<>))
                .As(typeof(IRemoteCaller<>))
                .SingleInstance();
            builder.RegisterAssemblyTypes(typeof(Program).Assembly)
                .Where(t => t.GetInterfaces().Any(x =>
                    x.IsGenericType &&
                    x.GetGenericTypeDefinition() == typeof(IRemoteCallConfiguration<>)))
                .AsImplementedInterfaces();
            builder.RegisterLogger();
            var container = builder.Build();

            Console.WriteLine(await container.Resolve<IRemoteCaller<IService1>>()
                    .Call(service => service.GetDataAsync(123)));

            Log.CloseAndFlush();
        }
    }

    public interface IRemoteCallConfiguration<T>
    {
        string Uri { get; }
        string UserName { get; }
        string Password { get; }
    }

    public class Service1Configuration : IRemoteCallConfiguration<IService1>
    {
        public string Uri => "http://localhost:59957/Service1.svc";
        public string UserName => "test";
        public string Password => "test";
    }

    public interface IRemoteCaller<out T>
    {
        Task<TResult> Call<TResult>(Func<T, Task<TResult>> func);
    }

    public class RemoteCaller<T>: IRemoteCaller<T>
    {
        private readonly ChannelFactory<T> _factory;

        public RemoteCaller(IRemoteCallConfiguration<T> configuration, ILogger logger)
        {
            var binding = new BasicHttpBinding();
            var endpoint = new EndpointAddress(configuration.Uri);
            //Если вместо ChannelFactory использовать ClientBase<T>, то отключается кеш,
            //потому что вызывается гетер свойства client.ClientCredentials.
            //В исходном коде видно, что в гетере свойства ClientCredentials вызывается
            //отключение кеша TryDisableSharing. Об этом можно прочитать по ссылке:
            //https://docs.microsoft.com/en-us/dotnet/framework/wcf/feature-details/channel-factory-and-caching
            var factory = new ChannelFactory<T>(binding, endpoint);
            factory.Endpoint.Behaviors.Add(new InspectorBehavior(logger));
            //Взято отсюда https://stackoverflow.com/a/8660551
            var clientCredentials = factory.Endpoint.Behaviors.Find<ClientCredentials>();
            clientCredentials.UserName.UserName = configuration.UserName;
            clientCredentials.UserName.Password = configuration.Password;
            _factory = factory;
        }

        public async Task<TResult> Call<TResult>(Func<T, Task<TResult>> func)
        {
            var channel = _factory.CreateChannel();
            //Почему важно закрывать клиента:
            //https://stackoverflow.com/a/7184555
            //https://stackoverflow.com/a/7271088
            //Если возникнет необходимость, то в будущем следует реализовать
            //более сложный вариант закрытия, как описано по ссылкам:
            //https://stackoverflow.com/a/573925
            //https://docs.microsoft.com/en-us/dotnet/framework/wcf/samples/avoiding-problems-with-the-using-statement
            //https://docs.microsoft.com/en-us/dotnet/framework/wcf/samples/expected-exceptions
            using ((IClientChannel) channel)
                return await func(channel);
        }
    }

    public class InspectorBehavior : IEndpointBehavior
    {
        private readonly ILogger _logger;

        public InspectorBehavior(ILogger logger)
        {
            _logger = logger;
        }

        public void Validate(ServiceEndpoint endpoint)
        {
        }

        public void AddBindingParameters(ServiceEndpoint endpoint, BindingParameterCollection bindingParameters)
        {
        }

        public void ApplyDispatchBehavior(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher)
        {
        }

        public void ApplyClientBehavior(ServiceEndpoint endpoint, ClientRuntime clientRuntime)
        {
            clientRuntime.MessageInspectors.Add(new Inspector(_logger));
        }

        private class Inspector : IClientMessageInspector
        {
            private readonly ILogger _logger;

            public Inspector(ILogger logger)
            {
                _logger = logger;
            }

            public object BeforeSendRequest(ref Message request, IClientChannel channel)
            {
                _logger.Information($@"Request:
{request}");
                return null;
            }

            public void AfterReceiveReply(ref Message reply, object correlationState)
            {
                _logger.Information($@"Reply:
{reply}");
            }
        }
    }
}

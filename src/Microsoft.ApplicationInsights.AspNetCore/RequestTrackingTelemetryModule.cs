namespace Microsoft.ApplicationInsights.AspNetCore
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Reflection;
    using System.Threading;
    using Microsoft.ApplicationInsights.AspNetCore.DiagnosticListeners;
    using Microsoft.ApplicationInsights.AspNetCore.Extensibility.Implementation.Tracing;
    using Microsoft.ApplicationInsights.AspNetCore.Extensions;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.AspNetCore.Hosting;

    /// <summary>
    /// Telemetry module tracking requests using Diagnostic Listeners.
    /// </summary>
    public class RequestTrackingTelemetryModule : ITelemetryModule, IObserver<DiagnosticListener>, IDisposable
    {
        // We are only interested in BeforeAction event from Microsoft.AspNetCore.Mvc source.
        // We are interested in Microsoft.AspNetCore.Hosting and Microsoft.AspNetCore.Diagnostics as well.
        // Below filter achieves acceptable performance, character 22 shoudl not be M unless event is BeforeAction.
        private static readonly Predicate<string> HostingPredicate = (string eventName) => (eventName != null) ? !(eventName[21] == 'M') || eventName == "Microsoft.AspNetCore.Mvc.BeforeAction" : false;
        private readonly object lockObject = new object();
        private readonly IApplicationIdProvider applicationIdProvider;

        private TelemetryClient telemetryClient;
        private ConcurrentBag<IDisposable> subscriptions;
        private HostingDiagnosticListener diagnosticListener;
        private bool isInitialized = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="RequestTrackingTelemetryModule"/> class.
        /// </summary>
        public RequestTrackingTelemetryModule() 
            : this(null)
        {
            this.CollectionOptions = new RequestCollectionOptions();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RequestTrackingTelemetryModule"/> class.
        /// </summary>
        /// <param name="applicationIdProvider">Provider to resolve Application Id</param>
        public RequestTrackingTelemetryModule(IApplicationIdProvider applicationIdProvider)
        {
            this.applicationIdProvider = applicationIdProvider;
            this.subscriptions = new ConcurrentBag<IDisposable>();
        }

        /// <summary>
        /// Gets or sets request collection options.
        /// </summary>
        public RequestCollectionOptions CollectionOptions { get; set; }

        /// <summary>
        /// Initializes the telemetry module.
        /// </summary>
        /// <param name="configuration">Telemetry configuration to use for initialization.</param>
        public void Initialize(TelemetryConfiguration configuration)
        {
            try
            {
                if (!this.isInitialized)
                {
                    lock (this.lockObject)
                    {
                        if (!this.isInitialized)
                        {
                            this.telemetryClient = new TelemetryClient(configuration);

                            bool enableNewDiagnosticEvents = true;
                            try
                            {
                                enableNewDiagnosticEvents = typeof(IWebHostBuilder).GetTypeInfo().Assembly.GetName().Version.Major >= 2;
                            }
                            catch (Exception)
                            {
                                // ignore any errors
                            }

                            this.diagnosticListener = new HostingDiagnosticListener(
                                configuration,
                                this.telemetryClient,
                                this.applicationIdProvider,
                                this.CollectionOptions.InjectResponseHeaders,
                                this.CollectionOptions.TrackExceptions,
                                this.CollectionOptions.EnableW3CDistributedTracing,
                                enableNewDiagnosticEvents);

                            this.subscriptions?.Add(DiagnosticListener.AllListeners.Subscribe(this));

                            this.isInitialized = true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                AspNetCoreEventSource.Instance.RequestTrackingModuleInitializationFailed(e.Message);
            }
        }

        /// <inheritdoc />
        void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
        {
            var subs = Volatile.Read(ref this.subscriptions);
            if (subs is null)
            {
                return;
            }

            if (this.diagnosticListener.ListenerName == value.Name)
            {
                subs.Add(value.Subscribe(this.diagnosticListener, HostingPredicate));
                this.diagnosticListener.OnSubscribe();
            }
        }

        /// <inheritdoc />
        void IObserver<DiagnosticListener>.OnError(Exception error)
        {
        }

        /// <inheritdoc />
        void IObserver<DiagnosticListener>.OnCompleted()
        {
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.Dispose(true);
        }

        /// <summary>
        /// Dispose the class.
        /// </summary>
        /// <param name="disposing">Indicates if this class is currently being disposed.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            var subs = Interlocked.Exchange(ref this.subscriptions, null);
            if (subs is null)
            {
                return;
            }

            foreach (var subscription in subs)
            {
                subscription.Dispose();
            }

            if (this.diagnosticListener != null)
            {
                this.diagnosticListener.Dispose();
            }
        }
    }
}
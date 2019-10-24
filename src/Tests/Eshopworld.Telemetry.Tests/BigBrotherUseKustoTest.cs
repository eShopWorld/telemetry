﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Eshopworld.Core;
using Eshopworld.Telemetry;
using Eshopworld.Telemetry.Kusto;
using Eshopworld.Tests.Core;
using FluentAssertions;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using Microsoft.Azure.Services.AppAuthentication;
using Moq;
using Polly;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable once CheckNamespace
public class BigBrotherUseKustoTest
{
    private readonly ITestOutputHelper _output;
    private readonly string _kustoName;
    private readonly string _kustoLocation;
    private readonly string _kustoDatabase;
    private readonly string _kustoTenantId;

    private readonly ICslQueryProvider _kustoQueryClient;
    private readonly ICslAdminProvider _kustoAdminClient;

    public BigBrotherUseKustoTest(ITestOutputHelper output)
    {
        _output = output;
        _kustoName = Environment.GetEnvironmentVariable("kusto_name", EnvironmentVariableTarget.Process);
        _kustoLocation = Environment.GetEnvironmentVariable("kusto_location", EnvironmentVariableTarget.Machine);
        _kustoDatabase = Environment.GetEnvironmentVariable("kusto_database", EnvironmentVariableTarget.Machine);
        _kustoTenantId = Environment.GetEnvironmentVariable("kusto_tenant_id", EnvironmentVariableTarget.Machine);

        if (_kustoName != null && _kustoLocation != null && _kustoDatabase != null && _kustoTenantId != null)
        {
            var kustoUri = $"https://{_kustoName}.{_kustoLocation}.kusto.windows.net";
            var token = new AzureServiceTokenProvider().GetAccessTokenAsync(kustoUri, string.Empty).Result;
            var kustoConnectionString =
                new KustoConnectionStringBuilder(kustoUri)
                {
                    FederatedSecurity = true,
                    InitialCatalog = _kustoDatabase,
                    Authority = _kustoTenantId,
                    ApplicationToken = token
                };

            _kustoQueryClient = KustoClientFactory.CreateCslQueryProvider(kustoConnectionString);
            _kustoAdminClient = KustoClientFactory.CreateCslAdminProvider(kustoConnectionString);
        }
    }

    [Theory, IsLayer1]
    [InlineData(IngestionClient.Direct)]
    [InlineData(IngestionClient.Queued)]
    public async Task Test_KustoTestEvent_StreamsToKusto(IngestionClient client)
    {
        _kustoQueryClient.Should().NotBeNull();
        var source = new CancellationTokenSource();

        var bb = new BigBrother("", "");

        var builder = bb.UseKusto()
            .WithCluster(_kustoName, _kustoLocation, _kustoDatabase, _kustoTenantId);

        if (client == IngestionClient.Queued)
            builder.RegisterType<KustoTestEvent>().WithQueuedClient().Build();
        else
            builder.RegisterType<KustoTestEvent>().WithDirectClient().Build();

        var evt = new KustoTestEvent();
        bb.Publish(evt);

        _output.WriteLine($"Event dispatched to BB with {client} client");

        await Policy.Handle<Exception>()
            .WaitAndRetryAsync(new[]
            {
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5)
            })
            .ExecuteAsync(async () =>
            {
                var reader = await _kustoQueryClient.ExecuteQueryAsync(
                    _kustoDatabase,
                    $"{nameof(KustoTestEvent)} | where {nameof(KustoTestEvent.Id)} == \"{evt.Id}\" | summarize count()",
                    ClientRequestProperties.FromJsonString("{}"));

                _output.WriteLine("Checking if event is in Kusto ...");

                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(1);

                _output.WriteLine("Event verified.");

                source.Cancel();
            });
    }



    [Fact, IsUnit]
    public void Test_ExceptionTelemetry_DoesntStream_ToKusto()
    {
        var bb = new Mock<BigBrother>();
        bb.Setup(x => x.HandleKustoEvent(It.IsAny<TelemetryEvent>())).Verifiable();
        bb.Setup(x => x.HandleKustoEvents(It.IsAny<IList<TelemetryEvent>>())).Verifiable();

        bb.Object.SetupKustoSubscription();
        bb.Object.Publish(new Exception().ToExceptionEvent());

        bb.Verify(x => x.HandleKustoEvent(It.IsAny<TelemetryEvent>()), Times.Never);
        bb.Verify(x => x.HandleKustoEvents(It.IsAny<IList<TelemetryEvent>>()), Times.Never);
    }

    [Fact, IsUnit]
    public void Test_TimedTelemetry_DoesntStream_ToKusto()
    {
        var bb = new Mock<BigBrother>();
        bb.Setup(x => x.HandleKustoEvent(It.IsAny<TelemetryEvent>())).Verifiable();
        bb.Setup(x => x.HandleKustoEvents(It.IsAny<IList<TelemetryEvent>>())).Verifiable();

        bb.Object.SetupKustoSubscription();
        bb.Object.Publish(new KustoTestTimedEvent());

        bb.Verify(x => x.HandleKustoEvent(It.IsAny<TelemetryEvent>()), Times.Never);
        bb.Verify(x => x.HandleKustoEvents(It.IsAny<IList<TelemetryEvent>>()), Times.Never);
    }

    [Fact, IsUnit]
    public void Test_MetricTelemetry_DoesntStream_ToKusto()
    {
        var bb = new Mock<BigBrother>();
        bb.Setup(x => x.HandleKustoEvent(It.IsAny<TelemetryEvent>())).Verifiable();
        bb.Setup(x => x.HandleKustoEvents(It.IsAny<IList<TelemetryEvent>>())).Verifiable();

        bb.Object.SetupKustoSubscription();
        bb.Object.Publish(new KustoTestMetricEvent());

        bb.Verify(x => x.HandleKustoEvent(It.IsAny<TelemetryEvent>()), Times.Never);
        bb.Verify(x => x.HandleKustoEvents(It.IsAny<IList<TelemetryEvent>>()), Times.Never);
    }
}

public class KustoTestEvent : DomainEvent
{
    public KustoTestEvent()
    {
        Id = Guid.NewGuid();
        SomeInt = new Random().Next(100);
        SomeStringOne = Lorem.GetSentence();
        SomeStringTwo = Lorem.GetSentence();
        SomeDateTime = DateTime.Now;
        SomeTimeSpan = TimeSpan.FromMinutes(new Random().Next(60));
    }

    public Guid Id { get; set; }

    public int SomeInt { get; set; }

    public string SomeStringOne { get; set; }

    public string SomeStringTwo { get; set; }

    public DateTime SomeDateTime { get; set; }

    public TimeSpan SomeTimeSpan { get; set; }
}

public class KustoTestTimedEvent : TimedTelemetryEvent { }

public class KustoTestMetricEvent : MetricTelemetryEvent { }

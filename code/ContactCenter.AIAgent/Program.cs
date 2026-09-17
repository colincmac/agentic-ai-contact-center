using System.Text.Json.Serialization;
using ContactCenter.AIAgent.Configuration;
using ContactCenter.AIAgent.Endpoints;
using ContactCenter.AIAgent.Middleware;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
//builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 131072);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DemoExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var demo = builder.AddBankingDemo();

if (demo.Enabled) { builder.Services.AddDemoWebhookAuthentication(demo); }

var app = builder.Build();


app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseWebSockets();

if (demo.Enabled) 
{ 
    app.UseAuthentication(); 
    app.UseAuthorization(); 
}

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment()) 
{ 
    app.MapOpenApi(); 
}

app.MapBankingDemo(demo);
app.MapDefaultEndpoints();

app.Run();

public partial class Program;

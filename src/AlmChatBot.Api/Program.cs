using System.Net.Http.Headers;
using AlmChatBot.Api.Configuration;
using AlmChatBot.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<QwenConfig>(builder.Configuration.GetSection(QwenConfig.SectionName));
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<ISqlGuardrailService, SqlGuardrailService>();
builder.Services.AddSingleton<IAlmSqlPlanner, AlmSqlPlanner>();
builder.Services.AddSingleton<IAlmDbService, AlmDbService>();
builder.Services.AddScoped<IAlmAnalysisService, AlmAnalysisService>();
builder.Services.AddScoped<IAlmBulletinService, AlmBulletinService>();
builder.Services.AddScoped<IAlmOrchestratorService, AlmOrchestratorService>();
builder.Services.AddScoped<IAlmCashflowReportService, AlmCashflowReportService>();

builder.Services.AddHttpClient<IQwenClientService, QwenClientService>((sp, client) =>
{
    var config = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<QwenConfig>>().Value;
    var baseUrl = (config.BaseUrl ?? "http://127.0.0.1:11434/v1").TrimEnd('/') + "/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromMinutes(8);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", "http://localhost");
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "ALM Chat Bot");
}).SetHandlerLifetime(TimeSpan.FromMinutes(15));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthorization();
app.MapGet("/rapor", () => Results.Redirect("/rapor.html"));
app.MapControllers();
app.Run();

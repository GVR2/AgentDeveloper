var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
// Порт НЕ задаємо тут, щоб Healer міг передати --urls або спрацював ASPNETCORE_URLS/launchSettings.json
app.Run();
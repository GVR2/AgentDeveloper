var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Urls.Clear();
app.Urls.Add("http://localhost:5178");
app.Run();

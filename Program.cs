using Microsoft.EntityFrameworkCore;
using TodoApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

var jwtKey = "12345678901234567890123456789012ABCD"; 
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

var builder = WebApplication.CreateBuilder(args);

// DB – לא נגעתי
builder.Services.AddDbContext<ToDoDbContext>(options =>
    options.UseMySql("name=ToDoDB", ServerVersion.Parse("9.5.0-mysql")));

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAllOrigins", builder =>
    {
        builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "הכניסי כאן את ה-JWT (בלי המילה Bearer)",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

app.UseCors("AllowAllOrigins");

// Swagger
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", () => "ToDo API is running.");

// ------------------------------------------------------
// 🔒 MIDDLEWARE להגנת JWT
// ------------------------------------------------------

app.Use(async (context, next) =>
{
    var path = context.Request.Path;

    if (path.StartsWithSegments("/login") || path.StartsWithSegments("/register"))
    {
        await next();
        return;
    }

    var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
    if (authHeader == null || !authHeader.StartsWith("Bearer "))
    {
        context.Response.StatusCode = 401;
        return;
    }

    var token = authHeader.Substring("Bearer ".Length);

    try
    {
        var handler = new JwtSecurityTokenHandler();
        var claims = handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true
        }, out var validatedToken);

        context.Items["userId"] = claims.FindFirst("userId")?.Value;

        await next();
    }
    catch
    {
        context.Response.StatusCode = 401;
    }
});

// ------------------------------------------------------
// 🔐 CRUD ITEMS – עם סינון לפי userId
// ------------------------------------------------------

// GET – רק משימות של המשתמש
app.MapGet("/items", async (HttpContext context, ToDoDbContext db) =>
{
    var userIdStr = context.Items["userId"]?.ToString();
    if (!int.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

    var items = await db.Items.Where(i => i.UserId == userId).ToListAsync();
    return Results.Ok(items);
});

// POST – מוסיף משימה ומקשר למשתמש
app.MapPost("/items", async (HttpContext context, ToDoDbContext db, Item item) =>
{
    var userIdStr = context.Items["userId"]?.ToString();
    if (!int.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

    item.UserId = userId;
    db.Items.Add(item);
    await db.SaveChangesAsync();
    return Results.Created($"/items/{item.Id}", item);
});

// PUT – עדכון משימה
app.MapPut("/items/{id}", async (int id, Item updatedItem, ToDoDbContext db) =>
{
    var item = await db.Items.FindAsync(id);
    if (item is null) return Results.NotFound();

    item.Name = updatedItem.Name;
    item.IsComplete = updatedItem.IsComplete;
    await db.SaveChangesAsync();

    return Results.Ok(item);
});

// DELETE – מחיקת משימה
app.MapDelete("/items/{id}", async (int id, ToDoDbContext db) =>
{
    var item = await db.Items.FindAsync(id);
    if (item is null) return Results.NotFound();

    db.Items.Remove(item);
    await db.SaveChangesAsync();

    return Results.NoContent();
});

// AUTH ONLY (לא מוגן)
app.MapPost("/register", async (ToDoDbContext db, User user) =>
{
    if (db.Users.Any(u => u.Username == user.Username))
        return Results.BadRequest("User already exists");

    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Ok("User created");
});

app.MapPost("/login", async (ToDoDbContext db, User user) =>
{
    var existingUser = db.Users
        .FirstOrDefault(u => u.Username == user.Username && u.PasswordHash == user.PasswordHash);

    if (existingUser == null)
        return Results.Unauthorized();

    var claims = new[]
    {
        new Claim("userId", existingUser.Id.ToString()),
        new Claim(ClaimTypes.Name, existingUser.Username)
    };

    var token = new JwtSecurityToken(
        claims: claims,
        expires: DateTime.UtcNow.AddHours(3),
        signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
    );

    return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
});

app.Run();

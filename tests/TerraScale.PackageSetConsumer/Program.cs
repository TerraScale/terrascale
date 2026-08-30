using Microsoft.EntityFrameworkCore;
using TerraScale.DotNet;
using TerraScale.EntityFrameworkCore;

var clientType = typeof(TerraScaleClient);
var entityFrameworkType = typeof(TerraScaleEntityFrameworkCoreOptions);
var options = new DbContextOptionsBuilder().UseTerraScaleSource().Options;

Console.WriteLine($"{clientType.FullName}|{entityFrameworkType.FullName}|{options.Extensions.Count()}");

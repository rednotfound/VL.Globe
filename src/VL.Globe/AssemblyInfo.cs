using System.Runtime.CompilerServices;
using VL.Core.Import;

[assembly: InternalsVisibleTo("VL.Globe.Tests")]

// Namespace VL is stripped, so a class in namespace VL named Globe is the category Globe, and
// classes in namespace VL.Globe sit under it.
[assembly: ImportAsIs(Namespace = "VL")]

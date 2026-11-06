// Global usings for PrintShopCompanion projects.
// This file ensures System.IO and System.Net.Http are available in all
// source files without needing explicit 'using' directives in each file.
// Required because UseWPF=true on Infrastructure changes the implicit usings
// and drops System.IO from the default set.

global using System.IO;
global using System.Net.Http;

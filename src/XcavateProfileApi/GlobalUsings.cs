// Hot Chocolate contributes an implicit global using for the HotChocolate namespace, which defines
// a Path type that collides with System.IO.Path. Pinning the alias keeps existing file-system code
// compiling unchanged; use HotChocolate.Path explicitly where a GraphQL path is meant.
global using Path = System.IO.Path;

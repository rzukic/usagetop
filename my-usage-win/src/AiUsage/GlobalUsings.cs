// The WPF SDK leaves these two out of its implicit usings, because System.IO.Path
// and System.Windows.Shapes.Path would collide. Every file here wants the IO one,
// and the shapes are always written out in full.
global using System.IO;
global using System.Net.Http;

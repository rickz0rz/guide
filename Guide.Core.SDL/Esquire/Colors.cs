namespace Guide.Core.SDL.Esquire;

// ESQ ran on an Amiga with a 12-bit palette, so each channel is a multiple of 17.
public static class Colors
{
    public static SDL3.SDL.Color Transparent = new() { A = 0, R = 0, G = 0, B = 0 };
    public static SDL3.SDL.Color Blue136 = new() { A = 255, R = 34, G = 51, B = 136 };
    public static SDL3.SDL.Color Blue51 = new() { A = 255, R = 0, G = 0, B = 51 };
    public static SDL3.SDL.Color Blue85 = new() { A = 255, R = 0, G = 0, B = 85 };
    public static SDL3.SDL.Color Black0 = new() { A = 255, R = 0, G = 0, B = 0 };
    public static SDL3.SDL.Color Black17 = new() { A = 255, R = 17, G = 17, B = 17 };
    public static SDL3.SDL.Color Gray85 = new() { A = 255, B = 85, G = 85, R = 85 };
    public static SDL3.SDL.Color Gray93 = new() { A = 255, R = 93, G = 93, B = 93 };
    public static SDL3.SDL.Color Shadow = new() { A = 255, R = 8, G = 8, B = 51 };
    public static SDL3.SDL.Color Gray170 = new() { A = 255, R = 170, G = 170, B = 170 };
    public static SDL3.SDL.Color Yellow = new() { A = 255, R = 204, G = 204, B = 0 };
}

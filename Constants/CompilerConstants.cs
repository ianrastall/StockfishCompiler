namespace StockfishCompiler.Constants;

public static class CompilerType
{
    public const string GCC = "gcc";
    public const string Clang = "clang";
    public const string MSVC = "msvc";
    public const string MinGW = "mingw";
}

public static class BuildTargets
{
    public const string ProfileBuild = "profile-build";
    public const string Build = "build";
    public const string Strip = "strip";
    public const string ConfigSanity = "config-sanity";
    public const string Analyze = "analyze";
    public const string Net = "net";
}

public static class SourceVersions
{
    public const string Master = "master";
    public const string Stable = "stable";
}

public static class Architectures
{
    public const string X86_64 = "x86-64";
    public const string X86_64_VNNI512 = "x86-64-vnni512";
    public const string X86_64_VNNI256 = "x86-64-vnni256";
    public const string X86_64_AVX512 = "x86-64-avx512";
    public const string X86_64_BMI2 = "x86-64-bmi2";
    public const string X86_64_AVX2 = "x86-64-avx2";
    public const string X86_64_SSE41_POPCNT = "x86-64-sse41-popcnt";
    public const string X86_64_SSSE3 = "x86-64-ssse3";
    public const string X86_64_SSE3_POPCNT = "x86-64-sse3-popcnt";
    public const string ARMV8 = "armv8";
    public const string APPLE_SILICON = "apple-silicon";
}

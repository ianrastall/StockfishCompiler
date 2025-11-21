using System.Globalization;
using System.Windows.Controls;

namespace StockfishCompiler.Converters;

/// <summary>
/// Ensures only integer input within a safe range is accepted by bindings.
/// </summary>
public class IntegerValidationRule : ValidationRule
{
    public int Minimum { get; set; } = 1;
    public int Maximum { get; set; } = 32;

    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        var text = value as string ?? value?.ToString() ?? string.Empty;
        if (!int.TryParse(text, NumberStyles.Integer, cultureInfo, out var parsed))
        {
            return new ValidationResult(false, "Enter a number");
        }

        if (parsed < Minimum || parsed > Maximum)
        {
            return new ValidationResult(false, $"Enter a value between {Minimum} and {Maximum}");
        }

        return ValidationResult.ValidResult;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FormGauntlet;

public partial class MainWindow : Window
{
    private int _attempts;

    public MainWindow() => InitializeComponent();

    private void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        _attempts++;
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text!.Trim().Split(' ').Length < 2)
            errors.Add("Full name: enter first and last name.");
        if (!Regex.IsMatch(EmailBox.Text ?? "", @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            errors.Add("Email address: not a valid email.");
        if (!Regex.IsMatch(PolicyBox.Text ?? "", @"^POL-\d{5}$"))
            errors.Add("Policy number: must match POL-#####.");
        if (!DateTime.TryParseExact(DateBox.Text ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            errors.Add("Incident date: use YYYY-MM-DD.");
        else if (date > DateTime.Today)
            errors.Add("Incident date: cannot be in the future.");
        if (!decimal.TryParse(AmountBox.Text ?? "", NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            errors.Add("Claim amount: enter a positive number.");
        else if (amount > 100000)
            errors.Add("Claim amount: over 100000 requires a phone claim.");
        if (TypeCombo.SelectedIndex < 0)
            errors.Add("Incident type: pick one.");
        if ((DescriptionBox.Text ?? "").Trim().Length < 20)
            errors.Add("Incident description: at least 20 characters.");
        if (ConsentCheck.IsChecked != true)
            errors.Add("Accuracy consent: you must confirm accuracy.");

        if (errors.Count > 0)
        {
            StatusText.Text = $"Rejected (attempt {_attempts}) — {errors.Count} error(s)";
            ErrorsText.Text = string.Join("\n", errors);
            ResultText.Text = "";
        }
        else
        {
            StatusText.Text = $"Accepted on attempt {_attempts}";
            ErrorsText.Text = "";
            ResultText.Text = $"Claim submitted. Reference CLM-{DateTime.Now:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}";
        }
    }
}

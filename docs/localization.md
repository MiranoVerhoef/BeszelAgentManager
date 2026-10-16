# Localization

## Choose a language

Open **Extra → Language → Switch language** and select **English (original)** or **简体中文**. After a changed value is saved successfully, a dialog offers **Restart now** or **Later**. Restarting applies the new language; choosing Later keeps the application open with the preference saved for its next launch. Selecting the already-saved value does not prompt.

English is the default for new and existing configurations without `ui_language`. The supported values are `en-US` and `zh-Hans`; unsupported values use English. Language changes do not enter the agent configuration fingerprint or alter service commands, environment variable names, log filter identifiers, or update trust.

## Maintain the application strings

- English source strings: `src/BeszelAgentManager.WinUI/Strings/en-US/Resources.resw`.
- Simplified Chinese strings: `src/BeszelAgentManager.WinUI/Strings/zh-Hans/Resources.resw`.
- XAML: use `Text="{l:Localize Key=ResourceKey}"` (or the control's `Content`, `Header`, or other text property), with `xmlns:l="using:BeszelAgentManager.WinUI.Services"`.
- C#: use `Localization.Get("ResourceKey")` or `Localization.Get("ResourceKey", value)` for a format string such as `Installed version: {0}`.

Keep resource keys identical across languages and unique without regard to case. Preserve numbered placeholders, technical literals such as `true`/`false`, environment variable names, and paths. Translate the complete sentence rather than joining translated fragments.

Persist and compare machine values independently of translated labels. Log filter items retain their original identifier in `Tag`; service states are translated only when displayed. Diagnostic logs, raw service/OS errors, and upstream release notes stay in their source language.

## Maintain the installer strings

The Inno Setup script offers English and Simplified Chinese. Project-specific messages are in its `[CustomMessages]` section. Standard Chinese wizard messages are vendored at `installer/Languages/ChineseSimplified.isl`, from the [Inno Setup 6.7.3 source tree](https://github.com/jrsoftware/issrc/blob/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl), with the translator attribution retained.

Installer language does not override the saved application language. Both Bundled and Lite installers include the same application translations.

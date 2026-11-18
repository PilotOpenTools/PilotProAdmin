namespace ProAdmin.Pages.Modules;

/// <summary>Форма «Отключить» модуля на контуре. <paramref name="Page"/> - имя модуля в адресе его страницы; null - список модулей.</summary>
public sealed record ModuleDisableForm(string Env, string EnvTitle, Guid Id, string Name, string? Protected, string? Page);

/// <summary>Кнопки «Включить» и «Удалить навсегда» отключённого модуля.</summary>
public sealed record ModuleDisabledActions(string Env, string EnvTitle, Guid Id, string Name, string? Page);

using ProAdmin.Migration;

namespace ProAdmin.Pages.Migration;

/// <summary>
/// Один выпадающий список выбора файла присланной базы (<c>_ImportFile</c>). Сначала идут файлы
/// подходящего вида, затем остальные - вид определяется по таблицам внутри файла, и ошибиться можно.
/// </summary>
/// <param name="Kind">Вид файла, который ждёт эта роль: Main, Chats или Journal.</param>
public sealed record ImportFileField(
    string Name,
    string Title,
    string Hint,
    string Kind,
    string? Selected,
    ImportSourcesDto Sources,
    bool Required);

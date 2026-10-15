# Project workbooks WPF module

`ProjectWorkbooksView.xaml` and `ProjectWorkbooksViewModel` provide the workbook UI for the native
WPF application. `IProjectWorkbookService` and `HttpProjectWorkbookService` connect it to the
SQLite-backed project workbook API.

The UI supports project/workbook navigation, workbook and sheet creation, row editing, video
association and recovery, project image search/upload/rename/selection, and row discussions and
image reviews. Workbook content exists only in SQLite; `.xlsx` files are not imported or used as
workbook storage.

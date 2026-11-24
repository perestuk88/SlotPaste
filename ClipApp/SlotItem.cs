namespace ClipApp
{
    // Одна строчка во вкладке «Ячейки»: номер ячейки и что в ней лежит
    public class SlotItem
    {
        public int Number { get; set; }
        public string Text { get; set; } = "";

        // true, если в ячейке ничего нет
        public bool IsEmpty => string.IsNullOrEmpty(Text);

        // то, что показываем в списке
        public string Preview => IsEmpty ? "(пусто)" : Text;
    }
}
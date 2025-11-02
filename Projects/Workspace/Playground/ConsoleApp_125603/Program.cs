Console.Write("Введіть число: ");
string? input = Console.ReadLine();
if (double.TryParse(input, out var number))
{
    Console.WriteLine($"Ви ввели: {number}");
}
else
{
    Console.WriteLine("Помилка: введіть число");
}
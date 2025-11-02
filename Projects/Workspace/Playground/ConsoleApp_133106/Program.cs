Console.Write("Введіть число: ");
string input = Console.ReadLine();
if(double.TryParse(input, out double number))
{
    Console.WriteLine($"Ви ввели: {number}");
}
else
{
    Console.WriteLine("Помилка: введіть число");
}
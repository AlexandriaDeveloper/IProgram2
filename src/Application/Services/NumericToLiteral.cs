using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Application.Services
{
    public class NumericToLiteral
    {

        private const string Zero = "صفر";
        private const string One = "واحد";
        private const string OneFemale = "واحدة";
        private const string Ahad = "أحد";
        private const string Ehda = "إحدى";
        private const string Two = "اثنان";
        private const string TwoFemales = "اثنتان";
        private const string Ethna = "اثنا";
        private const string Ethnta = "اثنتا";
        private const string Three = "ثلاثة";
        private const string Four = "أربعة";
        private const string Five = "خمسة";
        private const string Six = "ستة";
        private const string Seven = "سبعة";
        private const string Eight = "ثمانية";
        private const string Nine = "تسعة";
        private const string Ten = "عشرة";
        private const string Ten2 = "عشر";
        private const string Twenty = "عشرون";
        private const string Thirty = "ثلاثون";
        private const string Fourty = "أربعون";
        private const string Fifty = "خمسون";
        private const string Sixty = "ستون";
        private const string Seventy = "سبعون";
        private const string Eighty = "ثمانون";
        private const string Ninety = "تسعون";
        private const string Hundred = "مائة";
        private const string TwoHundreds = "مئتان";
        private const string Thousand = "ألف";
        private const string Thousands = "آلاف";
        private const string Million = "مليون";
        private const string Millions = "ملايين";
        private const string Pillion = "مليار";
        private const string Pillions = "مليارات";
        private const string Trillion = "تريليون";
        private const string Trillions = "تريليونات";
        private const string Quadrillion = "كدريليون";
        private const string Quadrillions = "كدريليونات";
        private const string Quintillion = "كوينتيليون";
        private const string Quintillions = "كوينتيليونات";

        private static Dictionary<long, string> namesMap = new Dictionary<long, string>();

        private static void Map()
        {
            if (namesMap.Count > 0)
            {
                return;
            }
            namesMap.Add(0, Zero);
            namesMap.Add(1, One);
            namesMap.Add(2, Two);
            namesMap.Add(3, Three);
            namesMap.Add(4, Four);
            namesMap.Add(5, Five);
            namesMap.Add(6, Six);
            namesMap.Add(7, Seven);
            namesMap.Add(8, Eight);
            namesMap.Add(9, Nine);
            namesMap.Add(10, Ten);
            namesMap.Add(20, Twenty);
            namesMap.Add(30, Thirty);
            namesMap.Add(40, Fourty);
            namesMap.Add(50, Fifty);
            namesMap.Add(60, Sixty);
            namesMap.Add(70, Seventy);
            namesMap.Add(80, Eighty);
            namesMap.Add(90, Ninety);
            namesMap.Add(100, Hundred);
            namesMap.Add(1000, Thousand);
            namesMap.Add((long)(Math.Pow(10, 6)), Million);
            namesMap.Add((long)(Math.Pow(10, 9)), Pillion);
            namesMap.Add((long)(Math.Pow(10, 12)), Trillion);
            namesMap.Add((long)(Math.Pow(10, 15)), Quadrillion);
            namesMap.Add((long)(Math.Pow(10, 18)), Quintillion);

        }

        private static string Parse(long a, bool Female, string SingleName, string PluralName)
        {
            Map();
            string buf = a.ToString();
            buf = SimulateStrReverse.StrReverse(buf);
            int index = 0;
            bool negative = (buf[buf.Length - 1] == '-');
            long len = negative ? buf.Length - 1 : buf.Length;

            string[] name = new string[len];
            long unitValue = 0;
            while (index < len)
            {
                var n = SimulateVal.Val(buf[index]);
                long decimalPos = index % 3;
                if (decimalPos == 0)
                {
                    unitValue = (long)Math.Pow(10, index);
                }
                long decimalPlace = (long)Math.Pow(10, decimalPos);
                switch (decimalPlace)
                {
                    case 1:
                        if (unitValue > 1 && index + 1 == len)
                        {
                            switch (n)
                            {
                                case 1:
                                    name[index] = namesMap[unitValue] + "، ";
                                    break;
                                case 2:
                                    name[index] = namesMap[unitValue] + ("ان") + "، ";
                                    break;
                                default:
                                    name[index] = PluralNames(namesMap[n], unitValue) + "، ";
                                    break;
                            }
                        }
                        else if (n < 3)
                        {
                            if (Female && n == 2 && index == 0)
                            {
                                name[index] = TwoFemales;
                            }
                            else
                            {
                                name[index] = namesMap[n];
                            }
                        }
                        else
                        {
                            name[index] = (Female && index < 3) ? namesMap[n].Substring(0, namesMap[n].Length - 1) : namesMap[n];
                        }
                        break;
                    case 10:
                        string tmp = name[index - 1];
                        if (n == 1)
                        {
                            if (tmp == One)
                            {
                                tmp = (Female && index < 3) ? Ehda : Ahad;
                            }
                            else if (tmp == Two || tmp == TwoFemales)
                            {
                                tmp = (Female && index < 3) ? Ethnta : Ethna;
                            }
                        }

                        if (unitValue > 1 && index + 1 == len)
                        {
                            if (n == 1 && tmp == Zero)
                            {
                                name[index] = PluralNames(Ten, unitValue) + "، ";
                            }
                            else if (n == 1)
                            {
                                name[index] = Ten2 + " " + namesMap[unitValue] + "، ";
                            }
                            else
                            {
                                name[index] = namesMap[n * 10] + " " + namesMap[unitValue] + "، ";
                            }
                        }
                        else
                        {
                            name[index] = namesMap[n * 10];
                            if (name[index - 1] == Zero)
                            {
                                if (n == 1 && Female && index < 3)
                                {
                                    name[index] = Ten2;
                                }
                            }
                            else
                            {
                                if (n == 1 && !(Female && index < 3))
                                {
                                    name[index] = Ten2;
                                }
                            }
                        }

                        if (n != 0)
                        {
                            name[index - 1] = name[index];
                            name[index] = tmp;
                        }

                        break;
                    case 100:
                        string s1 = null;
                        if (n > 2)
                        {
                            s1 = namesMap[n];
                            s1 = s1.Substring(0, s1.Length - (((n == 8) ? 2 : 1))) + Hundred;
                        }
                        else
                        {
                            s1 = (n == 2) ? TwoHundreds : namesMap[n * 100];
                        }
                        if (unitValue > 1 && name[index - 2] != Zero)
                        {
                            var X = (name[index - 2] == Ten2) ? Ten : name[index - 2];
                            foreach (var Elm in namesMap)
                            {
                                var val = Elm.Key;
                                if (namesMap[val] == X)
                                {
                                    if (val > 2 && val < 10 || val == 10 && name[index - 1] == Zero)
                                    {
                                        name[index - 2] = PluralNames(name[index - 2], unitValue) + "، ";
                                    }
                                    else if (s1 == Zero && name[index - 1] == Zero)
                                    {
                                        if (val == 1)
                                        {
                                            name[index - 2] = namesMap[unitValue] + "، ";
                                        }
                                        else if (val == 2)
                                        {
                                            name[index - 2] = namesMap[unitValue] + "ان، ";
                                        }
                                        else
                                        {
                                            name[index - 2] = name[index - 2] + " " + namesMap[unitValue] + "، ";
                                        }
                                    }
                                    else
                                    {
                                        name[index - 2] = name[index - 2] + " " + namesMap[unitValue] + "، ";
                                    }
                                    break;
                                }
                            }
                        }
                        else if (unitValue > 1 && n != 0)
                        {
                            if (s1 == TwoHundreds)
                            {
                                s1 = s1.TrimEnd('ن');
                            }
                            s1 += " " + namesMap[unitValue] + "،";
                        }

                        name[index] = s1;

                        break;
                }
                index += 1;
            }

            string s = "";
            for (long c = 0; c < len; c++)
            {
                if (name[c] == Zero)
                {
                    continue;
                }
                if (Female && c == 0 && name[c] == One)
                {
                    name[c] = OneFemale;
                }
                name[c] = name[c].Trim();
                if (s != "" && !((s.StartsWith(Ten2 + " ") || s.StartsWith(Ten)) && (!(name[c - 1] == Zero))))
                {
                    if (c > 0)
                    {
                        var X = name[c].Split(' ');
                        if (X.Length > 0)
                        {
                            switch (X[0])
                            {
                                case Ten2:
                                case Twenty:
                                case Thirty:
                                case Fourty:
                                case Fifty:
                                case Sixty:
                                case Seventy:
                                case Eighty:
                                case Ninety:
                                    name[c] += "ا";
                                    break;
                            }
                        }
                    }
                    s = name[c] + " و" + s;
                }
                else
                {
                    s = name[c] + " " + s;
                }
            }

            s = "(" + s.Trim().Trim('،').Replace("،ا ", "ا، ") + ")";
            if (SingleName != "" && PluralName != "")
            {
                long N = 0;
                var X = a.ToString();
                if (X.Length < 2)
                {
                    N = a;
                }
                else
                {
                    N = System.Convert.ToInt64(X.Substring(X.Length - 2, 2));
                }

                if (N == 0)
                {
                    if (a > 0)
                    {
                        if (s.EndsWith("ان" + ")"))
                        {
                            s = s.TrimEnd(')').TrimEnd('ن') + ")";
                        }
                        s += " " + SingleName;
                    }
                }
                else if (N < 11)
                {
                    switch (name[0])
                    {
                        case Zero:

                            break;
                        case One:
                        case OneFemale:
                            if (a == 1)
                            {
                                s = SingleName + " " + name[0];
                            }
                            else
                            {
                                s += " من ال" + PluralName;
                            }
                            break;
                        case Two:
                        case TwoFemales:
                            if (a == 2)
                            {
                                SingleName = SingleName.Replace("ة", "ت");
                                s = SingleName + "ان " + name[0];
                            }
                            else
                            {
                                s += " من ال" + PluralName;
                            }
                            break;
                        default:
                            s += " " + PluralName;
                            break;
                    }
                }
                else
                {
                    s += " " + SingleName + (SingleName.EndsWith("ة") ? "" : "ا");
                }
            }

            return ((s == "") ? Zero : ((negative ? "سالب " + s : s)).Trim());
        }

        private static string PluralNames(string Word, long unitValue)
        {
            if (unitValue == 1000)
            {
                return Word + " " + Thousands;
            }
            else if (unitValue == (Math.Pow(10, 6)))
            {
                return Word + " " + Millions;
            }
            else if (unitValue == (Math.Pow(10, 9)))
            {
                return Word + " " + Pillions;
            }
            else if (unitValue == (Math.Pow(10, 12)))
            {
                return Word + " " + Trillions;
            }
            else if (unitValue == (Math.Pow(10, 15)))
            {
                return Word + " " + Quadrillions;
            }
            else
            {
                return Word + " " + Quintillions;
            }
        }

        public static string Convert(double a, bool Female = false, string SingleName = "", string PluralName = "")
        {
            if ((int)(a) > long.MaxValue)
            {
                return "هذا العدد أكبر من القيمة العظمى التي يمكن تحويلها";
            }
            string[] array = a.ToString().Split('.');
            long i = System.Convert.ToInt64(array[0]);
            long f = (array.Length == 2) ? System.Convert.ToInt64(array[1]) : 0;

            long fractSize = (f > 0) ? array[1].Length : 0;

            string integralPart = (i != 0 || f == 0) ? Parse(i, Female, SingleName, PluralName) : "";

            string fractionalPart = (f > 0) ? Parse(f, false, "", "").TrimEnd(')') + " قرشا ) ".TrimStart('(') : "";
            // string fractionalPart = (f > 0) ? Parse(f, false, "", "").TrimEnd() + " قرشا ".TrimStart() : "";

            return integralPart + (((f * i != 0) ? " و " : "")) + fractionalPart;
        }

    }
    public static class SimulateVal
    {
        public static double Val(string expression)
        {
            if (expression == null)
                return 0;

            //try the entire string, then progressively smaller
            //substrings to simulate the behavior of VB's 'Val',
            //which ignores trailing characters after a recognizable value:
            for (int size = expression.Length; size > 0; size--)
            {
                double testDouble;
                if (double.TryParse(expression.Substring(0, size), out testDouble))
                    return testDouble;
            }

            //no value is recognized, so return 0:
            return 0;
        }
        public static double Val(object expression)
        {
            if (expression == null)
                return 0;

            double testDouble;
            if (double.TryParse(expression.ToString(), out testDouble))
                return testDouble;

            //VB's 'Val' function returns -1 for 'true':
            bool testBool;
            if (bool.TryParse(expression.ToString(), out testBool))
                return testBool ? -1 : 0;

            //VB's 'Val' function returns the day of the month for dates:
            System.DateTime testDate;
            if (System.DateTime.TryParse(expression.ToString(), out testDate))
                return testDate.Day;

            //no value is recognized, so return 0:
            return 0;
        }
        public static int Val(char expression)
        {
            int testInt;
            if (int.TryParse(expression.ToString(), out testInt))
                return testInt;
            else
                return 0;
        }
    }


    //----------------------------------------------------------------------------------------
    //	Copyright © 2003 - 2012 Tangible Software Solutions Inc.
    //	This class can be used by anyone provided that the copyright notice remains intact.
    //
    //	This class simulates the behavior of the classic VB 'StrReverse' function.
    //----------------------------------------------------------------------------------------
    public static class SimulateStrReverse
    {
        public static string StrReverse(string expression)
        {
            if (string.IsNullOrEmpty(expression))
                return string.Empty;

            string reversedString = string.Empty;
            for (int charIndex = expression.Length - 1; charIndex >= 0; charIndex--)
            {
                reversedString += expression[charIndex];
            }
            return reversedString;
        }
    }
}
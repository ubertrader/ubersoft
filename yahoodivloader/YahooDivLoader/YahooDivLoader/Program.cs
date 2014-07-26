using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.IO;
using System.Text.RegularExpressions;
using Broker;

namespace YahooDivLoader
{
    class Program
    {
        [STAThread]
        static int Main(string[] args)
        {   
            if (args.Length < 3)
            {
                WriteHelp();                
                return 1;
            }


            string Ticker = args[0];
            DateTime bDate = DateTime.Parse(args[1]);
            DateTime eDate = DateTime.Parse(args[2]);
            bool localmode = false;

            if (args.Length > 4)
            {
                if (String.Compare("-l", args[4].Trim(), true) == 0)
                    localmode = true;
            }

            List<CorpAction> corpactions = new List<CorpAction>();
            if (!localmode)
            {
                //for (int i = 0; i <= 132; i += 66)
                {
                    var ca = DownloadDivHist(Ticker, bDate, eDate);

                    foreach (var c in ca)
                    {
                        bool inlist = false;
                        foreach (var c2 in corpactions)
                        {
                            if (c.Date == c2.Date && c.ActionType == c2.ActionType)
                            {
                                inlist = true;
                                break;
                            }
                        }

                        if (!inlist)
                            corpactions.Add(c);
                    }
                }
                CorpAction.SaveToFile(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath) + "\\" + Ticker + ".csv", corpactions);
            }
            else
            {
                corpactions = CorpAction.ReadFromFile(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath) + "\\" + Ticker + ".csv");
            }
            
            

            if (args.Length > 3)
            {
                if(String.Compare("-a", args[3].Trim(), true) == 0)
                    ApplyAmiCorpAction(Ticker, corpactions);
            }
                       
            return 0;
        }

        static void WriteHelp()
        {
            Console.WriteLine("Yahoo DividendDownloader (c) Alex Vedeneev 2011");
            Console.WriteLine("Usage:\r\n");
            Console.WriteLine("YahooDivLoader.exe Ticker BeginDate EndDate [-a] [-l]\r\n");
            Console.WriteLine("     Ticker - ticker name according to Yahoo rules");
            Console.WriteLine("     BeginDate - dd.mm.yyyy");
            Console.WriteLine("     EndDate - dd.mm.yyyy");
            Console.WriteLine("     -a - [Optional] Automatically adjust splits and div in Amibroker");
            Console.WriteLine("     -l - [Optional] Local Mode in Amibroker (Uses Local Files)");
        }

        static List<CorpAction> DownloadDivHist(string Ticker, DateTime BeginDate, DateTime EndDate, int offset = 0)
        {
            //WebRequest request = WebRequest.Create(String.Format("http://finance.yahoo.com/q/hp?s={0}&a={1}&b={2}&c={3}&d={4}&e={5}&f={6}&g=v", Ticker, BeginDate.Month-1, BeginDate.Day, BeginDate.Year,EndDate.Month - 1, EndDate.Day, EndDate.Year));
        
            WebRequest request = WebRequest.Create(String.Format("http://finance.yahoo.com/q/hp?s={0}&a={1}&b={2}&c={3}&d={4}&e={5}&f={6}&g=m&z=66&y={7}", Ticker, BeginDate.Month - 1, BeginDate.Day, BeginDate.Year, EndDate.Month - 1, EndDate.Day, EndDate.Year, offset));
            WebResponse response = request.GetResponse();
            StreamReader reader = new StreamReader(response.GetResponseStream());
            string str = reader.ReadToEnd();


            // Parsing HTML
            string regex = "<tr><td\\sclass=\"yfnc_tabledata1\"\\snowrap\\salign=\"right\">(?<Date>.[^<]*)</td><td\\sclass=\"yfnc_tabledata1\"\\salign=\"center\"\\scolspan=\"6\">(?<Action>.[^<]*)</td></tr>";

             System.Text.RegularExpressions.RegexOptions options = ((System.Text.RegularExpressions.RegexOptions.IgnorePatternWhitespace | System.Text.RegularExpressions.RegexOptions.Multiline)
                               | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                   System.Text.RegularExpressions.Regex reg = new System.Text.RegularExpressions.Regex(regex, options);

                   

            MatchCollection corpactions = reg.Matches(str);
            string[] groupnames = reg.GetGroupNames();
            List<CorpAction> act = new List<CorpAction>();

            foreach (Match m in corpactions)
            {
                int j = 0;
                CorpAction ca = new CorpAction();
                foreach (Group g in m.Groups)
                {
                    switch (groupnames[j])
                    {
                        case "Date":
                            ca.Date = DateTime.Parse(g.ToString());
                            break;
                        case "Action":
                            if(g.ToString().Contains("Dividend") )
                            {
                                var s = g.ToString().Split(new char[] { ' ' });
                                ca.DivSplitRatio = float.Parse(s[0]);
                                ca.ActionType = 1;
                            }
                            else if (g.ToString().Contains("Split"))
                            {
                                var s = g.ToString().Substring(0, g.ToString().IndexOf("Stock"));
                                var ss = s.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                                float sp1 = float.Parse(ss[0]);
                                float sp2 = float.Parse(ss[1]);
                                ca.DivSplitRatio = sp2 / sp1;
                                ca.ActionType = -1;
                            }
                            break;
                    }
                    j++;
                }
                act.Add(ca);
            }

            act.Sort(CorpAction.CompareAction);
            return act;
        }
        
     
        /// <summary>
        /// Алгоритм изменения котировок Амиброкера с учетом корп. событий
        /// 1. Начиная от самой ранней даты корп события
        /// 2. Ищем индекс последней котировки дня корпоративного события
        ///     а. Если IO не равно 0 то игнорируем событие (считаем что оно уже учтено)
        ///     б. Если IO равно 0, то вносим коррективы для всех предыдущих котировок
        ///         - Рассчитываем поправочный коэффициент Div/Close, а коэф. Split используем напрямую
        ///         - Умножаем цену и объем всех предыдущих котировок на IO*попр. коэф (IO содержит данные о предыдущих корп событиях и их коэффициентах)
        ///         - Сохраняем новое значение IO = Старое IO * тек. попр. коэф.
        /// 3. Переходим к анализу след. корп события
        ///      
        /// </summary>
        /// <param name="Ticker"></param>
        /// <param name="act"></param>
        static void ApplyAmiCorpAction(string Ticker, List<CorpAction> act)
        {
            using (Broker.IApplication ab = new Amibroker())
            {
                Broker.IStocks stlist = new Stocks(ab.Stocks);
                IStock s = new Stock(stlist[Ticker]);

                IQuotations q = new Quotations(s.Quotations);
                DateTime[] DT = new DateTime[q.Count];
                float[] O = new float[q.Count];
                float[] H = new float[q.Count];
                float[] L = new float[q.Count];
                float[] C = new float[q.Count];
                float[] V = new float[q.Count];
                float[] OI = new float[q.Count];
                float[] Aux1 = new float[q.Count];
                float[] Aux2 = new float[q.Count];

                int cnt = q.RetrieveEx(q.Count, ref DT, ref O, ref H, ref L, ref C, ref V, ref OI, ref Aux1, ref Aux2);

                foreach (var ca in act)
                {
                    int caidx = -1;
                    float cadj = 0;

                    bool hasactdate = false;
                    DateTime PreActionDate = DateTime.MinValue;

                    for (int i = 0; i < cnt; i++)
                    {                        
                        if (DT[i].Date == ca.Date.Date)
                        {
                            hasactdate = true;
                        }
                        else if(hasactdate && PreActionDate == DateTime.MinValue)
                        {
                            PreActionDate = DT[i].Date;
                        }

                        if( (hasactdate && DT[i].Date == PreActionDate)
                            || (!hasactdate && DT[i].Date < ca.Date.Date && ca.ActionType == -1))
                        {
                            caidx = i;
                            if (ca.ActionType == 1)
                                cadj = 1 - ( ca.DivSplitRatio / C[i] );
                            else if (ca.ActionType == -1)
                                cadj = ca.DivSplitRatio;

                            //IO котировки в момент корп. события != 0 
                            //существует вероятность что событие учено, пропускаем!
                            if (OI[i] != 0)
                                caidx = -1;
                            break;
                        }
                    }

                    if (caidx == -1)
                        continue;

                    for (int i = caidx; i < cnt; i++)
                    {
                        //Quotation qq = new Quotation(q[i]);
                        O[i] *= cadj;
                        H[i] *= cadj;
                        L[i] *= cadj;
                        C[i] *= cadj;
                        

                        if (OI[i] == 0)
                            OI[i] = cadj;
                        else
                            OI[i] *= cadj;

                        //Volume меняем только в момент Split
                        if (ca.ActionType == -1)
                            V[i] = V[i] / cadj;

                        if (ca.ActionType == 1)
                        {                           
                            if (DT[i].Date == PreActionDate.Date)
                            {
                                Aux1[i] = ca.DivSplitRatio;
                            }
                        }
                        else if (ca.ActionType == -1)
                        {
                            //В Aux1 пишем объем дивидендов
                            Aux1[i] *= cadj;
                        }
                    }
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("$SEPARATOR ,\r\n");
                sb.AppendFormat("$FORMAT Ticker,Date_DMY,Time,Open,High,Low,Close,Volume,OpenInt,Aux1,Aux2\r\n");
                for (int i = 0; i < cnt; i++)
                {
                    //Пропускаем EOD котировки т.к. они уже SplitAdjusted
                    if (DT[i].Hour == 0 && DT[i].Minute == 0 && DT[i].Second == 0)
                        continue;


                    sb.AppendFormat("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10}\r\n",
                        Ticker,
                        DT[i].ToString("d"),
                        DT[i].ToString("HH:mm:ss"),
                        O[i],
                        H[i],
                        L[i],
                        C[i],
                        V[i],
                        OI[i],
                        Aux1[i],
                        Aux2[i]);

                }
                File.WriteAllText(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath) + "\\amiimportadj.csv", sb.ToString());
                ab.Import(0, Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath) + "\\amiimportadj.csv", "");
             
                ab.RefreshAll();
                ab.SaveDatabase();
            }
        }
    }

    class CorpAction
    {
        public DateTime Date;
        public float DivSplitRatio;
        public int ActionType;
        public float PrevDayClose;
        public float PrevDayAdjClose;

        public static int CompareAction(CorpAction x, CorpAction y)
        {
            return x.Date.CompareTo(y.Date);
        }

        public static void SaveToFile(string Path, List<CorpAction> act)
        {
            StringBuilder sb = new StringBuilder();

            foreach (var ca in act)
            {
                sb.AppendFormat("{0};{1};{2}\r\n", ca.Date, ca.ActionType, ca.DivSplitRatio);
            }

            File.WriteAllText(Path, sb.ToString());
        }

        public static List<CorpAction> ReadFromFile(string Path)
        {
            List<CorpAction> ca = new List<CorpAction>();

            if (!File.Exists(Path))
                return ca;

            string[] lines = File.ReadAllLines(Path);
            foreach (var l in lines)
            {
                string[] chunk = l.Split(new char[] { ';' });
                if (chunk.Length == 3)
                {
                    CorpAction c = new CorpAction();
                    c.Date = DateTime.Parse(chunk[0]);
                    c.ActionType = int.Parse(chunk[1]);
                    c.DivSplitRatio = float.Parse(chunk[2]);
                    ca.Add(c);
                }
            }
            return ca;
        }
    }

     
}

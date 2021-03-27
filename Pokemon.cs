using Network;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;

namespace Pokedex
{
    [Serializable]
    public class Pokemon
    {
        static Pokemon()
        {
            TotalEvolChains = TotalEvolutionChains();
        }

        private static Dictionary<int, Pokemon> Registry = new Dictionary<int, Pokemon>();
        public static event Action OnFullyLoad;

        public static int PokemonCount
        {
            get
            {
                return Registry.Keys.Count;
            }
        }

        private static int TotalEvolChains;
        public static Pokemon[] LoadedPokemon
        {
            get
            {
                return Registry.Values.ToArray();
            }
        }

        public static Pokemon Get(int id)
        {
            Pokemon o;
            Registry.TryGetValue(id, out o);
            return o;
        }

        private static int TotalEvolutionChains()
        {
            WebsiteParser c = new WebsiteParser();
            JObject evoData = JObject.Parse(c.ParseWebsite("https://pokeapi.co/api/v2/evolution-chain"));
            int count = (int) evoData["count"];
            evoData = JObject.Parse(c.ParseWebsite("https://pokeapi.co/api/v2/evolution-chain/?limit=1&offset=" + (count - 1)));
            string lastEvo = (string) evoData["results"][0]["url"];
            lastEvo = lastEvo.Substring(0, lastEvo.Length - 1);
            c.Close();

            int final = int.Parse(lastEvo.Substring(lastEvo.LastIndexOf("/") + 1));

            return final;
        }

        private static Pokemon DownloadPokemonFromEvo(JObject evoChain, WebsiteParser c)
        {
            string basicSite = (string) evoChain["species"]["url"];
            JObject site = JObject.Parse(c.ParseWebsite(basicSite));

            Pokemon basic = DownloadPokemonWithAllForms(site, c);
            JArray evolutions = (JArray) evoChain["evolves_to"];
            if (evolutions.Count == 0) return basic;

            List<Pokemon> chain = new List<Pokemon>();
            
            for (int i = 0; i < evolutions.Count; i++)
            {
                Pokemon newEvolution = DownloadPokemonFromEvo((JObject)evolutions[i], c);
                chain.Add(newEvolution);
            }

            basic.Evolutions = chain.ToArray();
            return basic;
        }

        private static Pokemon DownloadPokemonWithAllForms(JObject species, WebsiteParser c)
        {
            Pokemon b = new Pokemon(species, 0, c);
            b.DefaultPokemon = true;
            b.RootPokemon = null;

            JArray varietyList = (JArray)species["varieties"];
            b.Forms = new Pokemon[varietyList.Count - 1];

            for (int e = 1; e < varietyList.Count; e++)
            {
                Pokemon variety = new Pokemon(species, e, c);
                variety.DefaultPokemon = false;
                variety.RootPokemon = b;

                b.Forms[e - 1] = variety;
            }

            return b;
        }

        private static void AddToRegistry(Pokemon p)
        {
            Registry.Add(p.PokedexNumber, p);
            foreach (Pokemon evo in p.Evolutions)
            {
                AddToRegistry(evo);
            }
        }

        public static Pokemon[] FindByName(string name)
        {
            List<Pokemon> possibleMatches = new List<Pokemon>();
            if (string.IsNullOrWhiteSpace(name)) return null;
            bool found = false;
            Registry.Values.AsParallel().ForAll(p =>
            {
                if (found) return;
                if (p.Name.ToLower() == name.ToLower())
                {
                    possibleMatches.Add(p);
                    found = true;
                    return;
                }
                if (p.Name.ToLower().Contains(name.ToLower())) possibleMatches.Add(p);
            });
            if (found)
            {
                possibleMatches = possibleMatches.FindAll(p => p.Name.ToLower() == name.ToLower()).ToList();
            }
            return possibleMatches.ToArray();
        }

        public static void SavePokedex(string path)
        {
            BinaryFormatter formatter = new BinaryFormatter();
            Directory.CreateDirectory(path);
            using (FileStream fs = new FileStream(path + "poke.dex", FileMode.OpenOrCreate))
            {
                formatter.Serialize(fs, Registry);
            }
        }

        public static void DownloadPokedex(int threads = 8)
        {
            Thread[] list = new Thread[threads];
            float BatchTotal = TotalEvolChains / (float)threads;

            for (int i = 0; i < threads; i++)
            {
                int tmp = i;
                Thread t = new Thread(() =>
                {
                    int min = (int)Math.Round(tmp * BatchTotal);
                    int max = (int)Math.Round((tmp + 1) * BatchTotal);

                    WebsiteParser c = new WebsiteParser();
                    for (int number = min; number <= max; number++)
                    {
                        if (number <= 0) continue;
                        try
                        {
                            if (number > TotalEvolChains) continue;
                            JObject chainDat = JObject.Parse(c.ParseWebsite("https://pokeapi.co/api/v2/evolution-chain/" + number));
                            Pokemon b = DownloadPokemonFromEvo((JObject)chainDat["chain"], c);
                            AddToRegistry(b);
                        }
                        catch (Exception)
                        {
                        }

                        if (number == 554 || number == 555)
                        {
                            Console.WriteLine(number);
                        }
                    }

                    c.Close();
                });
                t.Name = "REG" + i;
                list[tmp] = t;
            }
            Array.ForEach(list, t => t.Start());
            Array.ForEach(list, t => t.Join());

            OnFullyLoad?.Invoke();
        }

        public static void LoadPokedex(string path)
        {
            BinaryFormatter bf = new BinaryFormatter();
            using (FileStream fs = new FileStream(path + "poke.dex", FileMode.OpenOrCreate))
            {
                Registry = (Dictionary<int, Pokemon>)bf.Deserialize(fs);
            }

            OnFullyLoad?.Invoke();
        }

        public static void SaveImages(string path)
        {
            Action<Pokemon> saveImg = (p) =>
            {
                p.MaleSprite?.Save(Path.Combine(path, p.PokedexNumber + (!string.IsNullOrWhiteSpace(p.FormName) ? "-" + p.FormName : "") + ".png"));
                p.MaleShinySprite?.Save(Path.Combine(path, p.PokedexNumber + (!string.IsNullOrWhiteSpace(p.FormName) ? "-" + p.FormName : "") + ".png"));
            };

            LoadedPokemon.AsParallel().ForAll(p =>
            {
                saveImg.Invoke(p);
                foreach (Pokemon form in p.Forms)
                {
                    saveImg.Invoke(form);
                }
            });
        }

        public string Name { get; protected set; }
        public int Generation { get; protected set; }
        public int PokedexNumber { get; protected set; }
        private Image MaleSprite;
        private string MaleSpriteLink;
        private Image FemaleSprite;
        private string FemaleSpriteLink;
        private Image MaleShinySprite;
        private string MaleShinySpriteLink;
        private Image FemaleShinySprite;
        private string FemaleShinySpriteLink;
        public EnumPokemonType TypeA { get; protected set; }
        public EnumPokemonType TypeB { get; protected set; }
        public string FormName { get; protected set; }
        public bool DefaultPokemon { get; protected set; }
        public Pokemon[] Forms { get; protected set; } = new Pokemon[0];
        public Pokemon[] Evolutions { get; protected set; } = new Pokemon[0];
        public bool BasicPokemon { get; protected set; }
        public EnumPokemonArctype ArcType { get; protected set; }
        public Pokemon RootPokemon { get; protected set; }

        public Image GetSprite(SpriteType t)
        {
            switch (t)
            {
                default:
                    return MaleSprite;
                case SpriteType.Male:
                    return MaleSprite;
                case SpriteType.Female:
                    return FemaleSprite == null ? MaleSprite : FemaleSprite;
                case SpriteType.MaleShiny:
                    return MaleShinySprite == null ? MaleSprite : MaleShinySprite;
                case SpriteType.FemaleShiny:
                    return FemaleShinySprite == null ? GetSprite(SpriteType.Female) : FemaleShinySprite;
            }
        }

        public Pokemon FindForm(string name)
        {
            foreach (Pokemon f in Forms)
            {
                if (f.FormName.ToLower() == name.ToLower()) return f;
            }
            return null;
        }

        public string GetSpriteLink(SpriteType t)
        {
            switch (t)
            {
                default:
                    return MaleSpriteLink;
                case SpriteType.Male:
                    return MaleSpriteLink;
                case SpriteType.Female:
                    return FemaleSprite == null ? MaleSpriteLink : FemaleSpriteLink;
                case SpriteType.MaleShiny:
                    return MaleShinySprite == null ? MaleSpriteLink : MaleShinySpriteLink;
                case SpriteType.FemaleShiny:
                    return FemaleShinySprite == null ? GetSpriteLink(SpriteType.Female) : FemaleShinySpriteLink;
            }
        }

        private void LoadImages(WebsiteParser c)
        {
            if (MaleSpriteLink != null) MaleSprite = c.ParseImage(MaleSpriteLink);
            if (MaleShinySpriteLink != null) MaleShinySprite = c.ParseImage(MaleShinySpriteLink);

            if (FemaleSpriteLink != null) FemaleSprite = c.ParseImage(FemaleSpriteLink);
            if (FemaleShinySpriteLink != null) FemaleShinySprite = c.ParseImage(FemaleShinySpriteLink);
        }

        private Pokemon()
        {
        }

        private Pokemon(JObject species, int varietyID, WebsiteParser client)
        {
            // Data universal to species
            PokedexNumber = (int)species["id"];
            // Find its name in English and use that
            foreach (JObject i in species["names"])
            {
                if (((string)i["language"]["name"]) == "en")
                {
                    Name = (string) i["name"];
                }
            }

            // Get its generation using the URL to the Generation its from
            string genString = (string)species["generation"]["url"];
            Generation = int.Parse(genString.Substring(genString.Length - 2, 1));

            // If it doesn't evolve from anything, it's a basic.
            BasicPokemon = species["evolves_from_species"].Type == JTokenType.Null;

            // Check if it's a legendary or mythical through JSON, or if its an UB by checking if its between certain Pokedex numbers
            ArcType = species.Value<bool>("is_legendary") ? EnumPokemonArctype.Legendary :
                species.Value<bool>("is_mythical") ? EnumPokemonArctype.Mythical :
                Enumerable.Range(793, 799).Contains(PokedexNumber) || Enumerable.Range(803, 806).Contains(PokedexNumber) ?
                EnumPokemonArctype.Mythical : EnumPokemonArctype.Normal;

            // Variety-specific data
            JArray varieties = (JArray)species["varieties"];
            varietyID = Math.Max(0, Math.Min(varietyID, varieties.Count - 1));

            // Get the variety-specific name, e.g. "mega", "gmax", "alolan"
            FormName = ((string)varieties[varietyID]["pokemon"]["name"]).Replace((string)species["name"], "");
            if (FormName.Length > 0)
            {
                FormName = FormName.Substring(1);
            }

            JObject pokemon = JObject.Parse(client.ParseWebsite((string) varieties[varietyID]["pokemon"]["url"]));

            // Types
            string typeAString = (string)pokemon["types"][0]["type"]["name"];
            typeAString = char.ToUpper(typeAString[0]) + typeAString.Substring(1);
            TypeA = (EnumPokemonType)Enum.Parse(typeof(EnumPokemonType), typeAString);

            if ((pokemon["types"] as JArray).Count > 1)
            {
                string typeBString = (string)pokemon["types"][1]["type"]["name"];
                typeBString = char.ToUpper(typeBString[0]) + typeBString.Substring(1);
                TypeB = (EnumPokemonType)Enum.Parse(typeof(EnumPokemonType), typeBString);
            }
            else
            {
                TypeB = EnumPokemonType.None;
            }

            // Sprites
            MaleSpriteLink = (string)pokemon["sprites"]["front_default"];
            MaleShinySpriteLink = (string)pokemon["sprites"]["front_shiny"];

            if ((bool)species["has_gender_differences"])
            {
                FemaleSpriteLink = (string)pokemon["sprites"]["front_female"];
                FemaleShinySpriteLink = (string)pokemon["sprites"]["front_shiny_female"];
            }

            LoadImages(client);
        }
    }

    public enum EnumFormType
    {
        Other, Mega, Gmax, Alola, Galar, Default
    }

    public enum EnumPokemonArctype
    {
        Normal, Legendary, Mythical, UltraBeast
    }

    public enum EnumPokemonType
    {
        None, Bug, Dark, Dragon, Electric, Fairy, Fighting, Fire,
        Flying, Ghost, Grass, Ground, Ice, Normal, Poison, Psychic,
        Rock, Steel, Water
    }

    public enum SpriteType
    {
        Male, Female, MaleShiny, FemaleShiny
    }
}

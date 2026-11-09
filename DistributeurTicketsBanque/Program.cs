using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DistributeurTickets
{
    internal static class Program
    {
        private const string NOM_FICHIER = "fnuméro.txt";

        // Textes des messages, créés UNE SEULE FOIS : seuls le ticket, le nombre en attente
        // et les accords (personne/personnes, attend/attendent) changent à chaque client
        private const string MODELE_MESSAGE_ATTENTE =
            "Votre numéro est {0}, et il y a {1} {2} qui {3} avant vous";

        // Personne n'attend : on ne parle pas de la file
        private const string MODELE_MESSAGE_PREMIER = "Votre numéro est {0}";

        private const int TICKET_MAX = 9999;          // capacité maximale par type et par jour
        private const int LONGUEUR_SAISIE_MAX = 60;
        private const int COMPTE_MIN = 1;
        private const int COMPTE_MAX = 30;
        private const int NOM_MIN = 2;
        private const int NOM_MAX = 40;
        private const int LARGEUR = 44;

        private static readonly char[] Types = { 'V', 'R', 'I' };

        // Numéro de compte : uniquement des lettres (A-Z, a-z) et des chiffres (0-9)
        private static readonly Regex CompteValide =
            new Regex(@"^[A-Za-z0-9]+$", RegexOptions.Compiled);

        private static readonly Regex CaracteresNom =
            new Regex(@"^[\p{L}\p{M}' \-]+$", RegexOptions.Compiled);

        // Un mot : lettres, avec ' ou - uniquement entre deux lettres (Jean-Pierre, N'Diaye)
        private static readonly Regex MotNom =
            new Regex(@"^\p{L}[\p{L}\p{M}]*(?:['\-]\p{L}[\p{L}\p{M}]*)*$", RegexOptions.Compiled);

        private static readonly Regex LigneCompteur =
            new Regex(@"^([VRI])=([0-9]{1,5})$", RegexOptions.Compiled);

        private static readonly List<Client> clients = new List<Client>();
        private static readonly object Verrou = new object();
        private static int clotureFaite = 0;

        private sealed record Client(char Type, string Compte, string Nom, string Prenom,
                                     string Ticket, DateTime Heure);

        private enum Etat { Ok, Annule, FinFlux }

        private static string CheminFichier => Path.Combine(Path.GetTempPath(), NOM_FICHIER);

        private static int Main()
        {
            ConfigurerConsole();
            Console.CancelKeyPress += SurCtrlC;
            AppDomain.CurrentDomain.ProcessExit += SurSortieProcessus;

            AfficherTitre();

            // Ouverture de la banque : fichier créé, tous les compteurs à zéro
            if (!EcrireCompteurs(CompteursNuls()))
            {
                AfficherMessage("Erreur : impossible de créer le fichier des numéros :", ConsoleColor.Red);
                AfficherMessage(CheminFichier, ConsoleColor.Red);
                return 1;
            }

            AfficherMessage("La banque est ouverte. Les compteurs de tickets sont à zéro.", ConsoleColor.Green);
            AfficherMessage($"Fichier des numéros : {CheminFichier}", ConsoleColor.DarkGray);

            while (true)
            {
                AfficherSeparateur();
                char type = LireChoixMenu();

                // Q : seule vraie sortie du programme, avec confirmation
                // (la fermeture remet les compteurs à zéro, c'est irréversible)
                if (type == 'Q')
                {
                    if (ConfirmerFermeture())
                        break;
                    continue;
                }

                Etat etat = TraiterClient(type);

                if (etat == Etat.FinFlux)
                    break;
                if (etat == Etat.Annule)
                    continue;

                // Ce client est servi. Un autre client peut arriver : on revient toujours
                // au menu, qui contient l'option Quitter. "Non" ne ferme PAS la banque.
                AfficherSeparateur();
                if (!DemanderOuiNon("Un autre client souhaite-t-il prendre un numéro maintenant ? (O/N)", false))
                    AfficherMessage("Client servi. La borne reste ouverte pour le prochain client.", ConsoleColor.DarkGray);
            }

            Cloturer();
            return 0;
        }

        // La question est coupée en deux lignes pour rester lisible sur une fenêtre étroite
        private static bool ConfirmerFermeture()
        {
            AfficherMessage("La liste des clients sera affichée et les compteurs remis à zéro.", ConsoleColor.Yellow);
            return DemanderOuiNon("Fermer la banque ? (O/N)", true);
        }

        // Ctrl+C / Ctrl+Break : fermeture propre de la banque (liste + remise à zéro)
        private static void SurCtrlC(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            AfficherMessage(string.Empty);

            if (Cloturer())
                Environment.Exit(0);
        }

        // Sortie brutale du processus (ex. fermeture de la fenêtre) : pas d'affichage possible,
        // mais on remet quand même le fichier à zéro si la fermeture n'a pas eu lieu
        private static void SurSortieProcessus(object? sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref clotureFaite, 1) == 1)
                return;

            lock (Verrou)
            {
                EcrireCompteurs(CompteursNuls());
            }
        }

        // ------------------------------------------------------------------
        // Un client : saisie des informations puis attribution du ticket
        // ------------------------------------------------------------------
        private static Etat TraiterClient(char type)
        {
            AfficherMessage($"Opération choisie : {NomType(type)}", ConsoleColor.Magenta);
            AfficherMessage("(tapez 'annuler' à tout moment pour abandonner ce ticket)", ConsoleColor.DarkGray);

            // Le numéro de compte est un string de lettres et de chiffres, gardé tel que tapé
            Etat etat = DemanderChamp("Numéro de compte (lettres et chiffres uniquement) :",
                                      ValiderCompte, s => s, out string compte);
            if (etat != Etat.Ok) return Abandonner(etat);

            etat = DemanderChamp("Nom :",
                                 s => ValiderNom(s, "Le nom"), s => s.ToUpperInvariant(), out string nom);
            if (etat != Etat.Ok) return Abandonner(etat);

            etat = DemanderChamp("Prénom :",
                                 s => ValiderNom(s, "Le prénom"), CapitaliserPrenom, out string prenom);
            if (etat != Etat.Ok) return Abandonner(etat);

            // Le numéro n'est attribué qu'ici : un ticket annulé ne consomme aucun numéro
            if (!TryAttribuerTicket(type, compte, nom, prenom, out Client? client, out int attente)
                || client == null)
                return Etat.Annule;

            AfficherTicket(client, attente);
            return Etat.Ok;
        }

        private static Etat Abandonner(Etat etat)
        {
            if (etat == Etat.Annule)
                AfficherMessage("Ticket annulé : aucun numéro n'a été attribué.", ConsoleColor.Yellow);
            return etat;
        }

        private static void AfficherTicket(Client client, int attente)
        {
            AfficherSeparateur();
            AfficherMessage($"Client : {client.Prenom} {client.Nom} - Compte : {client.Compte}", ConsoleColor.Gray);
            AfficherMessage(new string('*', LARGEUR), ConsoleColor.Green);
            AfficherMessage($"   TICKET : {client.Ticket}", ConsoleColor.Green);
            AfficherMessage(new string('*', LARGEUR), ConsoleColor.Green);
            AfficherMessage(ConstruireMessageTicket(client.Ticket, attente), ConsoleColor.Green);
        }

        // Construit le message du ticket à partir des textes uniques :
        //  - personne n'attend : "Votre numéro est V-1"
        //  - 1 personne        : "Votre numéro est V-2, et il y a 1 personne qui attend avant vous"
        //  - 2 personnes ou +  : "Votre numéro est V-3, et il y a 2 personnes qui attendent avant vous"
        private static string ConstruireMessageTicket(string ticket, int attente)
        {
            if (attente <= 0)
                return string.Format(CultureInfo.InvariantCulture, MODELE_MESSAGE_PREMIER, ticket);

            bool pluriel = attente > 1;

            return string.Format(CultureInfo.InvariantCulture, MODELE_MESSAGE_ATTENTE,
                                 ticket,
                                 attente,
                                 pluriel ? "personnes" : "personne",
                                 pluriel ? "attendent" : "attend");
        }

        // ------------------------------------------------------------------
        // Tickets et fichier fnuméro.txt
        // ------------------------------------------------------------------
        private static bool TryAttribuerTicket(char type, string compte, string nom, string prenom,
                                               out Client? client, out int attente)
        {
            client = null;
            attente = 0;

            lock (Verrou)
            {
                // Le fichier fait foi ; s'il est absent ou altéré, on repart de la mémoire
                Dictionary<char, int> compteurs = LireCompteurs() ?? CompterClients();
                Dictionary<char, int> memoire = CompterClients();

                foreach (char t in Types)
                    compteurs[t] = Math.Max(compteurs[t], memoire[t]);

                int rang = compteurs[type] + 1;
                if (rang > TICKET_MAX)
                {
                    AfficherMessage($"Capacité maximale atteinte pour {NomType(type)} aujourd'hui ({TICKET_MAX} tickets).",
                                    ConsoleColor.Red);
                    return false;
                }

                compteurs[type] = rang;

                if (!EcrireCompteurs(compteurs))
                {
                    AfficherMessage("Erreur : impossible d'écrire dans le fichier des numéros. Ticket non attribué.",
                                    ConsoleColor.Red);
                    return false;
                }

                client = new Client(type, compte, nom, prenom, $"{type}-{rang}", DateTime.Now);
                clients.Add(client);

                // Personne n'est servi dans cette simulation : tous les tickets précédents
                // du même type attendent encore
                attente = rang - 1;
                return true;
            }
        }

        // Retourne null si le fichier est absent, illisible ou altéré
        private static Dictionary<char, int>? LireCompteurs()
        {
            try
            {
                if (!File.Exists(CheminFichier))
                    return null;

                var resultat = new Dictionary<char, int>();

                foreach (string ligne in File.ReadAllLines(CheminFichier, Encoding.UTF8))
                {
                    Match m = LigneCompteur.Match(ligne.Trim());
                    if (!m.Success)
                        return null;

                    char t = m.Groups[1].Value[0];
                    if (resultat.ContainsKey(t))
                        return null;

                    resultat[t] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                }

                return resultat.Count == Types.Length ? resultat : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static bool EcrireCompteurs(Dictionary<char, int> compteurs)
        {
            try
            {
                string[] lignes = Types.Select(t => $"{t}={compteurs[t]}").ToArray();
                File.WriteAllLines(CheminFichier, lignes, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static Dictionary<char, int> CompteursNuls()
            => Types.ToDictionary(t => t, _ => 0);

        private static Dictionary<char, int> CompterClients()
            => Types.ToDictionary(t => t, t => clients.Count(c => c.Type == t));

        private static string NomType(char type) => type switch
        {
            'V' => "Versement",
            'R' => "Retrait",
            'I' => "Informations",
            _ => "Inconnu"
        };

        // ------------------------------------------------------------------
        // Fermeture : liste des clients de la journée puis remise à zéro
        // ------------------------------------------------------------------
        // Retourne true si c'est cet appel qui a fait la fermeture (une seule fois)
        private static bool Cloturer()
        {
            if (Interlocked.Exchange(ref clotureFaite, 1) == 1)
                return false;

            lock (Verrou)
            {
                AfficherListeClients();
                AfficherSeparateur();

                if (EcrireCompteurs(CompteursNuls()))
                    AfficherMessage("Banque fermée : les compteurs de tickets sont remis à zéro.", ConsoleColor.Cyan);
                else
                    AfficherMessage("Attention : le fichier n'a pas pu être remis à zéro. Il le sera à la prochaine ouverture.",
                                    ConsoleColor.Yellow);
            }

            return true;
        }

        private static void AfficherListeClients()
        {
            AfficherMessage("╔" + new string('═', LARGEUR - 2) + "╗", ConsoleColor.Cyan);
            AfficherMessage(Centrer("CLIENTS DE LA JOURNÉE"), ConsoleColor.Cyan);
            AfficherMessage("╚" + new string('═', LARGEUR - 2) + "╝", ConsoleColor.Cyan);

            if (clients.Count == 0)
            {
                AfficherMessage("Aucun client n'est passé aujourd'hui.", ConsoleColor.Yellow);
                return;
            }

            string[] entetes = { "N°", "Ticket", "Opération", "Compte", "Nom", "Prénom", "Heure" };

            List<string[]> lignes = clients
                .Select((c, i) => new[]
                {
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    c.Ticket,
                    NomType(c.Type),
                    c.Compte,
                    c.Nom,
                    c.Prenom,
                    c.Heure.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                })
                .ToList();

            int[] largeurs = entetes
                .Select((h, i) => Math.Max(h.Length, lignes.Max(l => l[i].Length)))
                .ToArray();

            AfficherMessage(FormaterLigne(entetes, largeurs), ConsoleColor.Cyan);
            AfficherMessage(string.Join("─┼─", largeurs.Select(w => new string('─', w))), ConsoleColor.DarkGray);

            foreach (string[] ligne in lignes)
                AfficherMessage(FormaterLigne(ligne, largeurs), ConsoleColor.Gray);

            AfficherMessage(string.Empty);

            string motClient = clients.Count > 1 ? "clients" : "client";
            AfficherMessage(
                $"Total : {clients.Count} {motClient} - " +
                string.Join(", ", Types.Select(t => $"{NomType(t)} : {clients.Count(c => c.Type == t)}")),
                ConsoleColor.Green);
        }

        private static string FormaterLigne(string[] cellules, int[] largeurs)
            => string.Join(" │ ", cellules.Select((c, i) => c.PadRight(largeurs[i])));

        // ------------------------------------------------------------------
        // ENTRÉES : tout passe par Lire() puis par une validation stricte
        // ------------------------------------------------------------------

        private static char LireChoixMenu()
        {
            AfficherMessage("Quel type d'opération souhaitez-vous effectuer ?", ConsoleColor.Cyan);
            AfficherMessage("  1 ou V - Versement", ConsoleColor.Gray);
            AfficherMessage("  2 ou R - Retrait", ConsoleColor.Gray);
            AfficherMessage("  3 ou I - Informations", ConsoleColor.Gray);
            AfficherMessage("  Q      - Quitter (fermer la banque)", ConsoleColor.Gray);

            while (true)
            {
                string? saisie = Lire(out string? anomalie);

                // Ctrl+Z / flux fermé : fermeture de la banque
                if (saisie == null)
                    return 'Q';

                switch (saisie.ToUpperInvariant())
                {
                    case "1":
                    case "V":
                        return 'V';
                    case "2":
                    case "R":
                        return 'R';
                    case "3":
                    case "I":
                        return 'I';
                    case "Q":
                        return 'Q';
                }

                AfficherMessage("Choix invalide : tapez 1 (Versement), 2 (Retrait), 3 (Informations) ou Q (Quitter).",
                                ConsoleColor.Yellow);
                AfficherAnomalie(anomalie);
            }
        }

        // valeurSiFluxFerme : réponse retenue si le flux d'entrée est fermé (Ctrl+Z)
        private static bool DemanderOuiNon(string question, bool valeurSiFluxFerme)
        {
            AfficherMessage(question, ConsoleColor.Cyan);

            while (true)
            {
                string? saisie = Lire(out string? anomalie);

                if (saisie == null)
                    return valeurSiFluxFerme;

                switch (saisie.ToUpperInvariant())
                {
                    case "O":
                    case "OUI":
                        return true;
                    case "N":
                    case "NON":
                        return false;
                }

                AfficherMessage("Réponse invalide : tapez 'O' (oui) ou 'N' (non).", ConsoleColor.Yellow);
                AfficherAnomalie(anomalie);
            }
        }

        // Pose une question et redemande tant que la saisie est invalide.
        //  - valider : retourne null si la saisie est valide, sinon le message d'erreur
        //  - formater : met la valeur validée en forme (majuscules, etc.)
        private static Etat DemanderChamp(string question, Func<string, string?> valider,
                                          Func<string, string> formater, out string valeur)
        {
            valeur = string.Empty;
            AfficherMessage(question, ConsoleColor.Cyan);

            while (true)
            {
                string? saisie = Lire(out string? anomalie);

                if (saisie == null)
                    return Etat.FinFlux;

                if (anomalie != null)
                {
                    AfficherMessage(anomalie, ConsoleColor.Yellow);
                    continue;
                }

                if (saisie.Equals("annuler", StringComparison.OrdinalIgnoreCase))
                    return Etat.Annule;

                string? erreur = valider(saisie);
                if (erreur != null)
                {
                    AfficherMessage(erreur, ConsoleColor.Yellow);
                    continue;
                }

                valeur = formater(saisie);
                return Etat.Ok;
            }
        }

        // Numéro de compte : suite de lettres (A-Z) et de chiffres (0-9), sans espace,
        // sans accent et sans symbole (comme " & @ - _ .)
        private static string? ValiderCompte(string s)
        {
            if (s.Length == 0)
                return "Le numéro de compte ne peut pas être vide.";

            if (s.Length < COMPTE_MIN || s.Length > COMPTE_MAX)
                return $"Le numéro de compte doit avoir entre {COMPTE_MIN} et {COMPTE_MAX} caractères.";

            if (!CompteValide.IsMatch(s))
                return "Le numéro de compte ne peut contenir que des lettres (A-Z) et des chiffres (0-9), " +
                       "sans espace, sans accent et sans symbole (comme \" ou &).";

            return null;
        }

        private static string? ValiderNom(string s, string libelle)
        {
            if (s.Length < NOM_MIN || s.Length > NOM_MAX)
                return $"{libelle} doit avoir entre {NOM_MIN} et {NOM_MAX} caractères.";

            if (!CaracteresNom.IsMatch(s))
                return $"{libelle} ne peut contenir que des lettres, espaces, tirets et apostrophes (pas de chiffres ni de symboles).";

            if (!s.Split(' ').All(mot => MotNom.IsMatch(mot)))
                return $"{libelle} contient un tiret ou une apostrophe mal placé.";

            return null;
        }

        // jean-pierre -> Jean-Pierre
        private static string CapitaliserPrenom(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool majuscule = true;

            foreach (char c in s.ToLowerInvariant())
            {
                sb.Append(majuscule ? char.ToUpperInvariant(c) : c);
                majuscule = c == ' ' || c == '-' || c == '\'';
            }

            return sb.ToString();
        }

        // Retourne :
        //  - null si le flux est fermé (Ctrl+Z / Ctrl+D) ou illisible
        //  - "" avec une anomalie expliquée si la saisie est trop longue ou contient des caractères de contrôle
        //  - sinon la saisie nettoyée (espaces de début/fin retirés, espaces multiples réduits)
        private static string? Lire(out string? anomalie)
        {
            anomalie = null;
            Ecrire("> ", ConsoleColor.Yellow, false);

            string? saisie;
            try
            {
                saisie = Console.ReadLine();
            }
            catch (IOException)
            {
                return null;
            }

            if (saisie == null)
                return null;

            saisie = saisie.Trim();

            if (saisie.Length > LONGUEUR_SAISIE_MAX)
            {
                anomalie = $"Saisie trop longue (maximum {LONGUEUR_SAISIE_MAX} caractères).";
                return string.Empty;
            }

            foreach (char c in saisie)
            {
                if (char.IsControl(c))
                {
                    anomalie = "Les caractères non imprimables sont refusés.";
                    return string.Empty;
                }
            }

            return Regex.Replace(saisie, @"\s+", " ").Replace('’', '\'');
        }

        // ------------------------------------------------------------------
        // SORTIES : tout passe par Ecrire() / AfficherMessage()
        // ------------------------------------------------------------------
        private static void AfficherMessage(string message, ConsoleColor couleur = ConsoleColor.Gray)
        {
            Ecrire(message, couleur, true);
        }

        private static void AfficherAnomalie(string? anomalie)
        {
            if (anomalie != null)
                AfficherMessage(anomalie, ConsoleColor.Yellow);
        }

        private static void AfficherTitre()
        {
            AfficherMessage("╔" + new string('═', LARGEUR - 2) + "╗", ConsoleColor.Cyan);
            AfficherMessage(Centrer("DISTRIBUTEUR DE TICKETS - BANQUE"), ConsoleColor.Cyan);
            AfficherMessage("╚" + new string('═', LARGEUR - 2) + "╝", ConsoleColor.Cyan);
        }

        private static string Centrer(string texte)
        {
            int interieur = LARGEUR - 2;
            int gauche = Math.Max(0, (interieur - texte.Length) / 2);
            int droite = Math.Max(0, interieur - gauche - texte.Length);
            return "║" + new string(' ', gauche) + texte + new string(' ', droite) + "║";
        }

        private static void AfficherSeparateur()
        {
            AfficherMessage(new string('─', LARGEUR), ConsoleColor.DarkGray);
        }

        private static void Ecrire(string texte, ConsoleColor couleur, bool saut)
        {
            try
            {
                Console.ForegroundColor = couleur;
                if (saut)
                    Console.WriteLine(texte);
                else
                    Console.Write(texte);
            }
            catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException)
            {
                // Sortie indisponible : on ne plante pas
            }
            finally
            {
                try
                {
                    Console.ResetColor();
                }
                catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException)
                {
                }
            }
        }

        private static void ConfigurerConsole()
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.InputEncoding = Encoding.UTF8;
            }
            catch (IOException)
            {
                // Encodage non modifiable : on garde celui par défaut
            }
        }
    }
}
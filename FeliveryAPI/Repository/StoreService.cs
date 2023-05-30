﻿using Azure.Core;
using FeliveryAPI.Data;
using FeliveryAPI.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Transactions;

namespace FeliveryAPI.Repository
{
    public class StoreService : IStoreService
    {
        private readonly UserManager<IdentityUser> _userManager;
        private readonly IConfiguration _IConfiguration;
        private readonly IWebHostEnvironment _environment;

        public IDbContextFactory<ElDbContext> Context { get; }

        public StoreService(UserManager<IdentityUser> userManager, IConfiguration _IConfig, IDbContextFactory<ElDbContext> context, IWebHostEnvironment environment)
        {
            _userManager = userManager;
            _IConfiguration = _IConfig;
            Context = context;
            _environment = environment;
        }
        public List<Restaurant> GetAll()
        {
            List<Restaurant> RestaurantsList = new();

            using (var customContext = Context.CreateDbContext())
            {
                RestaurantsList = customContext.Restaurants.ToList();
            }

            return RestaurantsList;
        }

        public Restaurant? GetDetails(int id)
        {
            Restaurant RestaurantDetails = new();
            using (var customContext = Context.CreateDbContext())
            {
                RestaurantDetails = customContext.Restaurants.Find(id);
            }
            return RestaurantDetails;

        }

        public async Task<IEnumerable<Order>> GetOrdersBystoreID(int storeID)
        {
            List<Order> Orders;
            using (var customContext = Context.CreateDbContext())
            {
                Orders = await customContext.Orders.Where(o => o.RestaurantID == storeID).ToListAsync();
            }
            return Orders;
        }


        public async Task<IEnumerable<MenuItem>> GetmenuitemsBystoreID(int storeID)
        {
            List<MenuItem> MenuItems;
            using (var customContext = Context.CreateDbContext())
            {
                MenuItems = await customContext.MenuItems.Include(m => m.Category).Where(m => m.RestaurantID == storeID).ToListAsync();
            }
            return MenuItems;
        }

        public async Task<IEnumerable<Category>> GetCategoriesBystoreID(int storeID)
        {
            List<Category> Categories;
            using (var customContext = Context.CreateDbContext())
            {
                Categories = await customContext.MenuItems.Where(m => m.RestaurantID == storeID).Select(m => m.Category).ToListAsync();
            }
            return Categories;
        }

        public void Update(Restaurant restaurant)
        {
            using var customContext = Context.CreateDbContext();
            var SecID = restaurant.SecurityID;
            customContext.Restaurants.Update(restaurant);
            restaurant.SecurityID = SecID;
            customContext.SaveChanges();
        }
        public void Delete(int id)
        {
            if (id == 0)
            {
                throw new Exception("ID is Invalid");
            }
            using var customContext = Context.CreateDbContext();
            if (customContext.Restaurants.Find(id) != null)
            {
                var RestaurantDetails = customContext.Restaurants.Find(id);
                customContext.Restaurants.Remove(customContext.Restaurants.Find(id));
                customContext.Users.Remove(customContext.Users.Find(RestaurantDetails.SecurityID));
                customContext.SaveChanges();
            }
            else
            {
                throw new Exception("Restaurant Not Found");
            }
        }

        public async Task<AuthModel> Register(RegData Data)
        {
            using TransactionScope transaction = new(TransactionScopeAsyncFlowOption.Enabled);
            try
            {
                TransactionManager.ImplicitDistributedTransactions = true;
                TransactionInterop.GetTransmitterPropagationToken(Transaction.Current);
                //Insert(Data.restaurant);
                //var result = RegisterAsync(Data.model);
                //Task.WaitAll(result);
                var res = await RegisterAsync(Data.Model);
                if (res.Message != null)
                {
                    throw new Exception(res.Message);
                }
                //string Img = UploadFile(Data.Image);
                Data.Restaurant.SecurityID = res.Id;
                //Data.Restaurant.StoreImg = Img;
                Insert(Data.Restaurant);
                transaction.Complete();
                return new AuthModel
                {
                    Username = res.Username,
                    Email = res.Email,
                    Roles = res.Roles,
                    IsAuthenticated = res.IsAuthenticated,
                    Token = res.Token,
                    ExpiresOn = res.ExpiresOn
                };
            }
            catch (Exception ex)
            {
                transaction.Dispose();
                return new AuthModel { Message = ex.Message };
            }
        }

        //-------

        public void Insert(Restaurant restaurant)
        {
            using var customContext = Context.CreateDbContext();
            customContext.Restaurants.Add(restaurant);
            customContext.SaveChanges();
        }
        public async Task<AuthModel> RegisterAsync(RegisterModel model)
        {
            if (await _userManager.FindByEmailAsync(model.Email) is not null)
                return new AuthModel { Message = "Email is already registered!" };

            if (await _userManager.FindByNameAsync(model.Username) is not null)
                return new AuthModel { Message = "Username is already registered!" };

            var user = new IdentityUser
            {
                UserName = model.Username,
                Email = model.Email
            };
            var result = await _userManager.CreateAsync(user, model.Password);

            if (!result.Succeeded)
            {
                var errors = string.Empty;

                foreach (var error in result.Errors)
                    errors += $"{error.Description},";

                return new AuthModel { Message = errors };
            }

            await _userManager.AddToRoleAsync(user, "PendingStore");

            var jwtSecurityToken = await CreateJwtToken(user);

            return new AuthModel
            {
                Id = user.Id,
                Email = user.Email,
                ExpiresOn = jwtSecurityToken.ValidTo,
                IsAuthenticated = true,
                Roles = new List<string> { "PendingStore" },
                Token = new JwtSecurityTokenHandler().WriteToken(jwtSecurityToken),
                Username = user.UserName
            };
        }

        private async Task<JwtSecurityToken> CreateJwtToken(IdentityUser user)
        {
            var userClaims = await _userManager.GetClaimsAsync(user);
            var roles = await _userManager.GetRolesAsync(user);
            var roleClaims = new List<Claim>();

            foreach (var role in roles)
                roleClaims.Add(new Claim("roles", role));

            var claims = new[]
            {
            new Claim(JwtRegisteredClaimNames.Sub, user.UserName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("uid", user.Id)
        }
            .Union(userClaims)
            .Union(roleClaims);

            var Key = _IConfiguration["JWT:key"];
            var issuer = _IConfiguration["JWT:issuer"];
            var audience = _IConfiguration["JWT:Audience"];
            var dur = _IConfiguration["JWT:DurationInDays"];
            var symmetricSecurityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
            var signingCredentials = new SigningCredentials(symmetricSecurityKey, SecurityAlgorithms.HmacSha256);

            var jwtSecurityToken = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                expires: DateTime.Now.AddDays(double.Parse(dur)),
                signingCredentials: signingCredentials);

            return jwtSecurityToken;
        }

        private string UploadFile(IFormFile Img)
        {
            if (Img != null)
            {
                string fileName = Guid.NewGuid().ToString() + "-" + Img.FileName;
                //string UploadDir = Path.Combine(_environment.WebRootPath, "\\Uploads\\Product\\", Img.FileName);
                //string filePath = Path.Combine(UploadDir, fileName);
                string filePath = Path.Combine(_environment.WebRootPath, "\\Uploads\\Product\\", fileName);
                string imagepath = filePath + "\\StoreImg.png";
                if (!Directory.Exists(filePath))
                    Directory.CreateDirectory(filePath);
                if (Directory.Exists(imagepath))
                    Directory.Delete(imagepath);
                //using FileStream stream = System.IO.File.Create(imagepath);
                //await file.CopyToAsync(stream);
                using (FileStream fileStream = new(filePath, FileMode.Create))
                {
                    Img.CopyTo(fileStream);
                }
                return filePath;
            }
            else
               throw new Exception("Image Not Found");
        }
    }
}

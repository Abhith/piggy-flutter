﻿using Abp.Application.Services.Dto;
using Abp.Authorization;
using Abp.AutoMapper;
using Abp.Domain.Repositories;
using Abp.Extensions;
using Code.Library;
using Microsoft.EntityFrameworkCore;
using Piggyvault.Notifications;
using Piggyvault.Notifications.Dto;
using Piggyvault.Piggy.Accounts;
using Piggyvault.Piggy.CurrencyRateExchange;
using Piggyvault.Piggy.Transactions.Dto;
using Piggyvault.Sessions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Piggyvault.Piggy.Transactions
{
    /// <summary>
    /// The transaction app service.
    /// </summary>
    [AbpAuthorize]
    public class TransactionAppService : PiggyvaultAppServiceBase, ITransactionAppService
    {
        /// <summary>
        /// The _account repository.
        /// </summary>
        private readonly IRepository<Account, Guid> _accountRepository;

        private readonly ICurrencyRateExchangeAppService _currencyRateExchangeService;
        private readonly INotificationAppService _notificationService;
        private readonly ISessionAppService _sessionAppService;
        private readonly IRepository<TransactionComment, Guid> _transactionCommentRepository;
        private readonly IRepository<Transaction, Guid> _transactionRepository;

        public TransactionAppService(IRepository<Transaction, Guid> transactionRepository, IRepository<Account, Guid> accountRepository, ISessionAppService sessionAppService, ICurrencyRateExchangeAppService currencyRateExchangeService, IRepository<TransactionComment, Guid> transactionCommentRepository, INotificationAppService notificationService)
        {
            _transactionRepository = transactionRepository;
            this._accountRepository = accountRepository;
            _sessionAppService = sessionAppService;
            this._currencyRateExchangeService = currencyRateExchangeService;
            _transactionCommentRepository = transactionCommentRepository;
            _notificationService = notificationService;
        }

        /// <summary>
        /// The copy transaction async.
        /// </summary>
        /// <param name="input">
        /// The input.
        /// </param>
        /// <returns>
        /// The <see cref="Task"/>.
        /// </returns>
        public async Task CopyTransactionAsync(Abp.Application.Services.Dto.EntityDto<Guid> input)
        {
            var baseTransaction = await this._transactionRepository.FirstOrDefaultAsync(t => t.Id == input.Id);
            var transctionDto = new TransactionEditDto();
            baseTransaction.MapTo(transctionDto);
            transctionDto.Id = null;
            transctionDto.TransactionTime = DateTime.UtcNow;

            await CreateOrUpdateTransaction(transctionDto);
        }

        [AbpAuthorize]
        public async Task CreateOrUpdateTransaction(TransactionEditDto input)
        {
            if (input.Id.HasValue)
            {
                await UpdateTransactionAsync(input);
            }
            else
            {
                await InsertTransactionAsync(input);
            }
        }

        public async Task CreateOrUpdateTransactionCommentAsync(TransactionCommentEditDto input)
        {
            if (input.Id.HasValue)
            {
                await UpdateTransactionCommentAsync(input);
            }
            else
            {
                await CreateTransactionCommentAsync(input);
            }
        }

        public async Task DeleteTransaction(Abp.Application.Services.Dto.EntityDto<Guid> input)
        {
            var tenantId = AbpSession.TenantId;
            var transaction = await _transactionRepository.FirstOrDefaultAsync(t => t.Id == input.Id && t.Account.TenantId == tenantId);
            if (transaction == null)
            {
                throw new AbpAuthorizationException("You are not authorized to perform the requested action");
            }
            await _transactionRepository.DeleteAsync(input.Id);
            await CurrentUnitOfWork.SaveChangesAsync();
            await UpdateTransactionsBalanceInAccountAsync(transaction.AccountId);
        }

        public async Task<Abp.Application.Services.Dto.ListResultDto<TransactionCommentPreviewDto>> GetTransactionComments(Abp.Application.Services.Dto.EntityDto<Guid> input)
        {
            var transctionComments = await _transactionCommentRepository.GetAll()
                .Where(c => c.TransactionId == input.Id).OrderBy(c => c.CreationTime).AsNoTracking().ToListAsync();

            return new Abp.Application.Services.Dto.ListResultDto<TransactionCommentPreviewDto>(transctionComments.MapTo<List<TransactionCommentPreviewDto>>());
        }

        public async Task<TransactionEditDto> GetTransactionForEdit(Abp.Application.Services.Dto.EntityDto<Guid> input)
        {
            var tenantId = AbpSession.TenantId;
            var transaction = await
                _transactionRepository.FirstOrDefaultAsync(t => t.Id == input.Id && t.Account.TenantId == tenantId);
            return transaction.MapTo<TransactionEditDto>();
        }

        /// <summary>
        /// The get transactions async.
        /// </summary>
        /// <param name="input">
        /// The input.
        /// </param>
        /// <returns>
        /// The <see cref="Task"/>.
        /// </returns>
        public async Task<Abp.Application.Services.Dto.PagedResultDto<TransactionPreviewDto>> GetTransactionsAsync(GetTransactionsInput input)
        {
            var output = new Abp.Application.Services.Dto.PagedResultDto<TransactionPreviewDto>();

            IQueryable<Transaction> query;

            var tenantId = AbpSession.TenantId;
            switch (input.Type)
            {
                case "tenant":
                    query = _transactionRepository.GetAll()
                                        .Include(t => t.Account)
                                        .Include(t => t.Category)
                                        .Include(t => t.Account.Currency).Where(t => t.Account.TenantId == tenantId);
                    break;

                case "user":
                    var userId = input.UserId ?? AbpSession.UserId;
                    query = _transactionRepository.GetAll()
                    .Include(t => t.Account)
                    .Include(t => t.Category)
                    .Include(t => t.Account.Currency).Where(t => t.CreatorUserId == userId);
                    break;

                case "account":
                    // TODO : NULL | Account owner tenant validation
                    query = _transactionRepository.GetAll()
                    .Include(t => t.Account)
                    .Include(t => t.Category)
                    .Include(t => t.Account.Currency).Where(t => t.AccountId == input.AccountId.Value);
                    break;

                default:
                    query = _transactionRepository.GetAll()
                    .Include(t => t.Account)
                    .Include(t => t.Category)
                    .Include(t => t.Account.Currency).Where(t => t.Account.TenantId == tenantId);
                    break;
            }

            // transaction period
            DateTime startDate;
            DateTime endDate;

            if (input.StartDate.HasValue && input.EndDate.HasValue)
            {
                startDate = input.StartDate.Value.Date;
                endDate = input.EndDate.Value.Date.AddDays(1).AddTicks(-1);
            }
            else
            {
                startDate = DateTime.Today.FirstDayOfMonth();
                endDate = startDate.AddMonths(1).AddTicks(-1);
            }

            // search
            if (!string.IsNullOrWhiteSpace(input.Query))
            {
                query = query.Where(t => t.Description.Contains(input.Query));
            }

            var transactions = await query.Where(t => t.TransactionTime >= startDate && t.TransactionTime <= endDate).OrderByDescending(t => t.TransactionTime).ThenByDescending(t => t.CreationTime).ToListAsync();

            output.Items = _currencyRateExchangeService.GetTransactionsWithAmountInDefaultCurrency(transactions).ToList();

            return output;
        }

        Task<Abp.Application.Services.Dto.PagedResultDto<TransactionPreviewDto>> ITransactionAppService.GetTransactionsAsync(GetTransactionsInput input)
        {
            throw new NotImplementedException();
        }

        public async Task<Abp.Application.Services.Dto.ListResultDto<string>> GetTypeAheadSuggestionsAsync(GetTypeAheadSuggestionsInput input)
        {
            var descriptionList = await _transactionRepository.GetAll()
                    .Where(t => t.Description.Contains(input.Query) && t.CreatorUserId == AbpSession.UserId)
                    .GroupBy(t => t.Description)
                    .Select(t => t.FirstOrDefault().Description).ToListAsync();

            return new Abp.Application.Services.Dto.ListResultDto<string>(descriptionList);
        }

        Task<Abp.Application.Services.Dto.ListResultDto<string>> ITransactionAppService.GetTypeAheadSuggestionsAsync(GetTypeAheadSuggestionsInput input)
        {
            throw new NotImplementedException();
        }

        public async Task ReCalculateAllAccountsTransactionBalanceOfUserAsync()
        {
            var userId = AbpSession.UserId;
            var userAccounts = await _accountRepository.GetAll().Where(a => a.CreatorUserId == userId).ToListAsync();

            foreach (var account in userAccounts)
            {
                await UpdateTransactionsBalanceInAccountAsync(account.Id);
            }
        }

        /// <summary>
        /// The send notification async.
        /// </summary>
        /// <param name="transactionId">
        /// The transaction id.
        /// </param>
        /// <returns>
        /// The <see cref="Task"/>.
        /// </returns>
        public async Task SendNotificationAsync(Guid transactionId)
        {
            var transaction = await _transactionRepository.GetAll().Include(t => t.Account).Include(t => t.Account.Currency).Include(t => t.Category).Include(t => t.CreatorUser).Where(t => t.Id == transactionId).FirstOrDefaultAsync();

            var transactionPreviewDto = transaction.MapTo<TransactionPreviewDto>();

            var notificationHeading = transaction.Amount > 0 ? "Inflow" : "Outflow";
            var notificationHeadingFromOrTo = transaction.Amount > 0 ? "to" : "from";

            var amount = transaction.Amount > 0 ? transaction.Amount : -transaction.Amount;

            notificationHeading += $" of {transaction.Account.Currency.Symbol} {amount} {notificationHeadingFromOrTo} {transaction.CreatorUser.UserName.ToPascalCase()}'s {transaction.Account.Name}";

            await _notificationService.SendPushNotificationAsync(new PushNotificationInput()
            {
                Contents = transaction.Description,
                Data = GetTransactionDataInDictionary(transactionPreviewDto),
                Headings = notificationHeading
            });
        }

        public async Task TransferAsync(TransferEditDto input)
        {
            // make sure expense
            if (input.Amount > 0)
            {
                input.Amount *= -1;
            }

            var sentTransaction = new TransactionEditDto()
            {
                Amount = input.Amount,
                AccountId = input.AccountId,
                CategoryId = input.CategoryId,
                Description = input.Description,
                TransactionTime = input.TransactionTime
            };

            await InsertTransactionAsync(sentTransaction, true);

            // make sure income
            if (input.ToAmount < 0)
            {
                input.ToAmount *= -1;
            }

            var senderAccount = await this._accountRepository.FirstOrDefaultAsync(a => a.Id == input.AccountId);

            var receivedTransaction = new TransactionEditDto()
            {
                Amount = input.ToAmount,
                AccountId = input.ToAccountId,
                CategoryId = input.CategoryId,
                Description = $"Received from {senderAccount.Name}",
                TransactionTime = input.TransactionTime
            };

            await InsertTransactionAsync(receivedTransaction, true);
        }

        private static Dictionary<string, string> GetTransactionDataInDictionary(TransactionPreviewDto transactionPreviewDto)
        {
            return new Dictionary<string, string>
            {
                {"AccountName", transactionPreviewDto.Account.Name},
                {"CreatorUserName", transactionPreviewDto.CreatorUserName},
                {"CategoryName", transactionPreviewDto.Category.Name},
                {"CategoryIcon", transactionPreviewDto.Category.Icon},
                {"Amount", transactionPreviewDto.Amount.ToString(CultureInfo.InvariantCulture)},
                {"TransactionTime", transactionPreviewDto.TransactionTime.ToString("s", CultureInfo.InvariantCulture)},
                {"Description", transactionPreviewDto.Description},
                {"TransactionId",transactionPreviewDto.Id.ToString() }
            };
        }

        private async Task<Result> CreateTransactionCommentAsync(TransactionCommentEditDto input)
        {
            try
            {
                var transactionComment = input.MapTo<TransactionComment>();
                await _transactionCommentRepository.InsertAndGetIdAsync(transactionComment);
                return await SendTransactionCommentPushNotificationAsync(input);
            }
            catch (Exception exception)
            {
                return Result.Fail(exception.Message);
            }
        }

        private async Task InsertTransactionAsync(TransactionEditDto input, bool isTranfer = false)
        {
            var transaction = input.MapTo<Transaction>();
            transaction.Balance = 0;
            transaction.IsTransferred = isTranfer;

            var transactionId = await _transactionRepository.InsertAndGetIdAsync(transaction);
            await CurrentUnitOfWork.SaveChangesAsync();

            await UpdateTransactionsBalanceInAccountAsync(input.AccountId);

            await this.SendNotificationAsync(transactionId);
        }

        private async Task<Result> SendTransactionCommentPushNotificationAsync(TransactionCommentEditDto input)
        {
            try
            {
                var currentUser = await _sessionAppService.GetCurrentLoginInformations();

                var transaction = await _transactionRepository.GetAll().Include(t => t.Account).Include(t => t.Account.Currency).Include(t => t.Category).Include(t => t.CreatorUser).Where(t => t.Id == input.TransactionId).FirstOrDefaultAsync();

                var transactionPreviewDto = transaction.MapTo<TransactionPreviewDto>();

                return await _notificationService.SendPushNotificationAsync(new PushNotificationInput()
                {
                    Contents = input.Content,
                    Headings = input.Id.HasValue
                         ? $"{currentUser.User.UserName.ToPascalCase()} updated a comment on a transaction done by {transactionPreviewDto.CreatorUserName}"
                         : $"New comment added by {currentUser.User.UserName.ToPascalCase()} on a transaction done by {transactionPreviewDto.CreatorUserName}.",
                    Data = GetTransactionDataInDictionary(transactionPreviewDto)
                });
            }
            catch (Exception exception)
            {
                return Result.Fail(exception.Message);
            }
        }

        private async Task UpdateTransactionAsync(TransactionEditDto input)
        {
            var tenantId = AbpSession.TenantId;
            var transaction = await _transactionRepository.FirstOrDefaultAsync(t => t.Id == input.Id && t.Account.TenantId == tenantId);
            input.MapTo(transaction);

            await CurrentUnitOfWork.SaveChangesAsync();
            await UpdateTransactionsBalanceInAccountAsync(input.AccountId);
        }

        private async Task<Result> UpdateTransactionCommentAsync(TransactionCommentEditDto input)
        {
            var transactionComment = await _transactionCommentRepository.FirstOrDefaultAsync(c => c.Id == input.Id.Value);

            input.MapTo(transactionComment);

            return await SendTransactionCommentPushNotificationAsync(input);
        }

        private async Task UpdateTransactionsBalanceInAccountAsync(Guid accountId)
        {
            var transactions = await
                    this._transactionRepository.GetAll()
                        .Where(t => t.AccountId == accountId)
                        .OrderBy(t => t.TransactionTime)
                        .ThenBy(t => t.CreationTime)
                        .ToListAsync();

            decimal currentBalance = 0;

            foreach (var transaction in transactions)
            {
                currentBalance += transaction.Amount;

                if (transaction.Balance == currentBalance)
                {
                    continue;
                }

                transaction.Balance = currentBalance;
                await this.CurrentUnitOfWork.SaveChangesAsync();
            }
        }
    }
}
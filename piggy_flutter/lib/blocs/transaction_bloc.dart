import 'dart:async';
import 'package:piggy_flutter/blocs/bloc_provider.dart';
import 'package:piggy_flutter/models/recent_transactions_state.dart';
import 'package:piggy_flutter/models/transaction_comment.dart';
import 'package:piggy_flutter/models/transaction_summary.dart';
import 'package:piggy_flutter/services/transaction_service.dart';
import 'package:rxdart/rxdart.dart';
import 'package:piggy_flutter/models/transaction_group_item.dart';

class TransactionBloc implements BlocBase {
  final TransactionService _transactionService = TransactionService();

  final _comment = BehaviorSubject<String>();

  final _syncSubject = PublishSubject<bool>();

  final _transactionCommentsRefresh = PublishSubject<String>();
  final _transactionComments = PublishSubject<List<TransactionComment>>();
  final _transactionsGroupBy =
      BehaviorSubject<TransactionsGroupBy>(seedValue: TransactionsGroupBy.Date);
  final _transactionSummary = BehaviorSubject<TransactionSummary>();
  final _recentTransactionsState = BehaviorSubject<RecentTransactionsState>();

  Stream<bool> get syncStream => _syncSubject.stream;

  Stream<String> get comment => _comment.stream.transform(validateComment);
  Stream<TransactionsGroupBy> get transactionsGroupBy =>
      _transactionsGroupBy.stream;
  Stream<List<TransactionComment>> get transactionComments =>
      _transactionComments.stream;
  Stream<TransactionSummary> get transactionSummary =>
      _transactionSummary.stream;
  Stream<RecentTransactionsState> get recentTransactionsState =>
      _recentTransactionsState.stream;

  Function(String) get changeComment => _comment.sink.add;
  Function(TransactionsGroupBy) get changeTransactionsGroupBy =>
      _transactionsGroupBy.sink.add;

  Function(bool) get sync => _syncSubject.sink.add;
  Function(String) get transactionCommentsRefresh =>
      _transactionCommentsRefresh.sink.add;

  TransactionBloc() {
//    print("########## TransactionBloc");
    _syncSubject.stream.listen(_handleSync);
    _transactionCommentsRefresh.stream.listen(getTransactionComments);
    _transactionsGroupBy.stream.listen(onTransactionsGroupByChanged);
  }

  submitComment(String transactionId) async {
    // print("########## TransactionBloc submitComment");
    final validComment = _comment.value;
    await _transactionService.saveTransactionComment(
        transactionId, validComment);
    transactionCommentsRefresh(transactionId);
    changeComment('');
  }

  void onTransactionsGroupByChanged(TransactionsGroupBy groupBy) async {
    print(
        '########## TransactionBloc onTransactionsGroupByChanged groupBy $groupBy');
    await getRecentTransactions();
  }

  Future<Null> getTransactionComments(String id) async {
    // print("########## TransactionBloc getTransactionComments");
    var result = await _transactionService.getTransactionComments(id);
    _transactionComments.add(result);
  }

  Future<Null> getRecentTransactions() async {
    print(
        "########## TransactionBloc getRecentTransactions ${_recentTransactionsState.value}");
    if (_recentTransactionsState.value is! RecentTransactionsPopulated) {
      _recentTransactionsState.add(RecentTransactionsLoading());
    }

    try {
      var result = await _transactionService.getTransactions(
          GetTransactionsInput(
              type: 'tenant',
              accountId: null,
              startDate: DateTime.now().add(Duration(days: -30)),
              endDate: DateTime.now().add(Duration(days: 1)),
              groupBy: _transactionsGroupBy.value));

      if (result.isEmpty) {
        _recentTransactionsState.add(RecentTransactionsEmpty());
      } else {
        _recentTransactionsState.add(RecentTransactionsPopulated(result));
      }
    } catch (e) {
      _recentTransactionsState.add(RecentTransactionsError());
    }
  }

  Future<Null> getTransactionSummary() async {
//    print("########## TransactionBloc getTransactionSummary");
    var result = await _transactionService.getTransactionSummary('month');
    _transactionSummary.add(result);
  }

  final validateComment = StreamTransformer<String, String>.fromHandlers(
      handleData: (comment, sink) {
    // TODO
    // if (comment.isEmpty) {
    //   sink.addError('Comment cannot be empty');
    // } else {
    sink.add(comment);
    // }
  });

  void dispose() {
    _transactionSummary.close();
    _syncSubject.close();
    _recentTransactionsState.close();
    _transactionComments.close();
    _transactionCommentsRefresh.close();
    _comment.close();
    _transactionsGroupBy.close();
  }

  void _handleSync(bool event) async {
    await getRecentTransactions();
    await getTransactionSummary();
  }
}